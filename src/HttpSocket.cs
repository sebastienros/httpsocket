﻿using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace HttpSocket
{
    class Program
    {
        private static ReadOnlySpan<byte> Http11 => Encoding.ASCII.GetBytes("HTTP/1.1");
        private static ReadOnlySpan<byte> ContentLength => Encoding.ASCII.GetBytes("Content-Length:");
        private static ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };

        static async Task Main(string[] args)
        {
            var serverUrl = "http://10.197.175.74:5001"; // new Uri(Environment.GetEnvironmentVariable("SERVER_URL"));
            // http://10.0.0.102:5000/plaintext

            // TODO: parse server url
            string hostName = "10.197.175.74";
            int hostPort = 5001;

            var request = $"GET {serverUrl} HTTP/1.1\r\n" +
                $"Host: {hostName}:{hostPort}\r\n" +
                "Content-Length: 0\r\n" +
                "\r\n";

            var requestBytes = Encoding.UTF8.GetBytes(request).AsMemory();

            IPAddress hostAddress = IPAddress.Parse(hostName);
            IPEndPoint hostEndPoint = new IPEndPoint(hostAddress, hostPort);

            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(hostEndPoint);

                var pipe = new Pipe();
                Task writing = FillPipeAsync(socket, pipe.Writer);

                try
                {
                    for (var k = 0; k < 10; k++)
                    {
                        var httpResponse = new HttpResponse();

                        var response = await socket.SendAsync(requestBytes, SocketFlags.None);

                        await ReadPipeAsync(pipe.Reader, httpResponse);

                        Console.WriteLine($"Result: {httpResponse.State}, Status: {httpResponse.StatusCode}, Content-Length: {httpResponse.ContentLength}");

                        // Stop sending request if the communication faced a problem (socket error)
                        if (httpResponse.State != HttpResponseState.Completed)
                        {
                            break;
                        }
                    }                    
                }
                finally
                {
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                }
            }
        }

        static async Task FillPipeAsync(Socket socket, PipeWriter writer)
        {
            const int minimumBufferSize = 512;

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);

                int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);

                // Indicates that the server is done with sending more data
                if (bytesRead == 0)
                {
                    break;
                }

                // Tell the PipeWriter how much was read from the Socket
                writer.Advance(bytesRead);

                // Make the data available to the PipeReader
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Tell the PipeReader that there's no more data coming
            writer.Complete();
        }

        static async Task ReadPipeAsync(PipeReader reader, HttpResponse httpResponse)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                var buffer = result.Buffer;

                ParseHttpResponse(ref buffer, httpResponse, out var examined);

                reader.AdvanceTo(buffer.Start, examined);

                // Stop when the response is complete
                if (httpResponse.State == HttpResponseState.Completed)
                {
                    break;
                }

                // Stop if there is incorrect data
                if (httpResponse.State == HttpResponseState.Error)
                {
                    // Incomplete request, close the connection with an error
                    break;
                }

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                {
                    if (httpResponse.State != HttpResponseState.Completed)
                    {
                        // Incomplete request, close the connection with an error
                        break;
                    }
                }
            }
        }

        static void ParseHttpResponse(ref ReadOnlySequence<byte> buffer, HttpResponse httpResponse, out SequencePosition examined)
        {
            var sequenceReader = new SequenceReader<byte>(buffer);
            examined = buffer.End;

            switch (httpResponse.State)
            {
                case HttpResponseState.StartLine:

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> version, (byte)' ') || !version.SequenceEqual(Http11))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> statusCodeText, (byte)' ') || !Utf8Parser.TryParse(statusCodeText, out int statusCode, out _))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }
                    else
                    {
                        httpResponse.StatusCode = statusCode;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> statusText, NewLine))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    httpResponse.State = HttpResponseState.Headers;

                    examined = sequenceReader.Position;

                    break;

                case HttpResponseState.Headers:

                    // Read evey headers
                    while (sequenceReader.TryReadTo(out var headerLine, NewLine))
                    {
                        // Is that the end of the headers?
                        if (headerLine.Length == 0)
                        {
                            examined = sequenceReader.Position;

                            // End of headers
                            httpResponse.State = httpResponse.HasContentLengthHeader
                                ? httpResponse.State = HttpResponseState.Body
                                : httpResponse.State = HttpResponseState.ChunkedBody;

                            break;
                        }

                        // Parse the header
                        ParseHeader(headerLine, httpResponse);
                    }

                    examined = sequenceReader.Position;
                    break;

                case HttpResponseState.Body:
                    
                    for (var i = 0; i < httpResponse.ContentLength; i++)
                    {
                        if (!sequenceReader.TryRead(out _))
                        {
                            httpResponse.State = HttpResponseState.Error;
                            return;
                        }
                    }

                    // The Body should end with two NewLine
                    if (!sequenceReader.TryReadTo(out _, NewLine) || !sequenceReader.TryReadTo(out _, NewLine))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    examined = sequenceReader.Position;
                    httpResponse.State = HttpResponseState.Completed;

                    break;

                case HttpResponseState.ChunkedBody:

                    while (true)
                    {
                        if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> chunkSizeText, (byte)'\n') || !Utf8Parser.TryParse(chunkSizeText, out int chunkSize, out _))
                        {
                            httpResponse.State = HttpResponseState.Error;
                            return;
                        }

                        httpResponse.ContentLength += chunkSize;

                        // The last chunk is always of size 0
                        if (chunkSize == 0)
                        {
                            // The Body should end with two NewLine
                            if (!sequenceReader.TryReadTo(out _, NewLine))
                            {
                                httpResponse.State = HttpResponseState.Error;
                                return;
                            }

                            examined = sequenceReader.Position;
                            httpResponse.State = HttpResponseState.Completed;

                            break;
                        }

                        for (var i = 0; i < chunkSize; i++)
                        {
                            if (!sequenceReader.TryRead(out _))
                            {
                                httpResponse.State = HttpResponseState.Error;
                                return;
                            }
                        }

                        if (!sequenceReader.TryReadTo(out _, NewLine))
                        {
                            httpResponse.State = HttpResponseState.Error;
                            return;
                        }
                    }

                    break;
            }

            // Slice whatever we've read so far
            buffer = buffer.Slice(sequenceReader.Position);
        }
        
        static void ParseHeader(in ReadOnlySequence<byte> headerLine, HttpResponse httpResponse)
        {
            var sequenceReader = new SequenceReader<byte>(headerLine);

            if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> header, (byte)' '))
            {
                httpResponse.State = HttpResponseState.Error;
                return;
            }

            // If this is the Content-Length header, read its value
            if (header.SequenceEqual(ContentLength))
            {
                httpResponse.HasContentLengthHeader = true;

                if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> contentLengthText, (byte)'\n') || !Utf8Parser.TryParse(contentLengthText, out int contentLength, out _))
                {
                    httpResponse.State = HttpResponseState.Error;
                    return;
                }
                else
                {
                    httpResponse.ContentLength = contentLength;
                }
            }
        }

        private enum HttpResponseState
        {
            StartLine,
            Headers,
            Body,
            ChunkedBody,
            Completed,
            Error
        }

        private class HttpResponse
        {
            public HttpResponseState State { get; set; } = HttpResponseState.StartLine;
            public int StatusCode { get; set; }
            public int ContentLength { get; set; }
            public bool HasContentLengthHeader { get; set; }
        }
    }
}