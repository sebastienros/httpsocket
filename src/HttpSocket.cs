using System;
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
        private static ReadOnlySpan<byte> ContentLength => Encoding.ASCII.GetBytes("Content-Length");
        private static ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };

        static async Task Main(string[] args)
        {
            // var serverUrl = "http://10.197.175.74:5001"; // new Uri(Environment.GetEnvironmentVariable("SERVER_URL"));
            // http://10.0.0.102:5000/plaintext

            // TODO: parse server url
            string hostName = "10.197.175.74";
            // string hostName = "127.0.0.1";
            int hostPort = 5001;

            var request = $"GET / HTTP/1.1\r\n" +
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
                    for (var k = 0; k < 100000; k++)
                    {
                        var httpResponse = new HttpResponse();

                        var response = await socket.SendAsync(requestBytes, SocketFlags.None);

                        await ReadPipeAsync(pipe.Reader, httpResponse);

                        Console.WriteLine($"Result: {httpResponse.State}, Status: {httpResponse.StatusCode}, Content-Length: {httpResponse.ContentLength}, Reads: {httpResponse.Reads}");

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

                httpResponse.Reads++;

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

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> version, (byte)' '))
                    {
                        return;
                    }

                    if (!version.SequenceEqual(Http11))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> statusCodeText, (byte)' '))
                    {
                        return;
                    }
                    else if (!Utf8Parser.TryParse(statusCodeText, out int statusCode, out _))
                    {
                        httpResponse.State = HttpResponseState.Error;
                    }
                    else
                    {
                        httpResponse.StatusCode = statusCode;
                    }

                    if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> statusText, NewLine))
                    {
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

                    if (httpResponse.ContentLengthRemaining > 0)
                    {
                        var bytesToRead = Math.Min(httpResponse.ContentLengthRemaining, sequenceReader.Remaining);

                        httpResponse.ContentLengthRemaining -= bytesToRead;

                        sequenceReader.Advance(bytesToRead);

                        examined = sequenceReader.Position;
                    }

                    if (httpResponse.ContentLengthRemaining == 0)
                    {
                        httpResponse.State = HttpResponseState.Completed;
                    }

                    break;

                case HttpResponseState.ChunkedBody:

                    while (true)
                    {
                        // Do we need to continue reading a active chunk?
                        if (httpResponse.LastChunkRemaining > 0)
                        {
                            var bytesToRead = Math.Min(httpResponse.LastChunkRemaining, sequenceReader.Remaining);

                            httpResponse.LastChunkRemaining -= (int)bytesToRead;

                            sequenceReader.Advance(bytesToRead);

                            if (httpResponse.LastChunkRemaining > 0)
                            {
                                examined = sequenceReader.Position;
                                // We need to read more data
                                break;
                            }
                            else if (!sequenceReader.TryReadTo(out _, NewLine))
                            {
                                httpResponse.State = HttpResponseState.Error;
                                break;
                            }

                            examined = sequenceReader.Position;
                        }
                        else
                        {
                            if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> chunkSizeText, NewLine))
                            {
                                // Don't have a full chunk yet
                                break;
                            }

                            if (!TryParseChunkPrefix(chunkSizeText, out int chunkSize))
                            {
                                httpResponse.State = HttpResponseState.Error;
                                break;
                            }

                            httpResponse.ContentLength += chunkSize;
                            httpResponse.LastChunkRemaining = chunkSize;

                            // The last chunk is always of size 0
                            if (chunkSize == 0)
                            {
                                // The Body should end with two NewLine
                                if (!sequenceReader.TryReadTo(out _, NewLine))
                                {
                                    httpResponse.State = HttpResponseState.Error;
                                    break;
                                }

                                examined = sequenceReader.Position;
                                httpResponse.State = HttpResponseState.Completed;

                                break;
                            }
                        }
                    }

                    break;
            }

            // Slice whatever we've read so far
            buffer = buffer.Slice(sequenceReader.Position);
        }

        private static void ParseHeader(in ReadOnlySequence<byte> headerLine, HttpResponse httpResponse)
        {
            var headerReader = new SequenceReader<byte>(headerLine);

            if (!headerReader.TryReadTo(out ReadOnlySpan<byte> header, (byte)':'))
            {
                httpResponse.State = HttpResponseState.Error;
                return;
            }

            // If this is the Content-Length header, read its value
            if (header.SequenceEqual(ContentLength))
            {
                httpResponse.HasContentLengthHeader = true;

                var remaining = headerReader.Sequence.Slice(headerReader.Position);

                if (remaining.Length > 128)
                {
                    // Giant number?
                    httpResponse.State = HttpResponseState.Error;
                    return;
                }

                if (!TryParseContentLength(remaining, out int contentLength))
                {
                    httpResponse.State = HttpResponseState.Error;
                    return;
                }

                httpResponse.ContentLength = contentLength;
                httpResponse.ContentLengthRemaining = contentLength;
            }
        }

        private static bool TryParseChunkPrefix(in ReadOnlySequence<byte> chunkSizeText, out int chunkSize)
        {
            if (chunkSizeText.IsSingleSegment)
            {
                if (!Utf8Parser.TryParse(chunkSizeText.FirstSpan, out chunkSize, out _, 'x'))
                {
                    return false;
                }
            }
            else
            {
                Span<byte> chunkSizeTextSpan = stackalloc byte[128];
                chunkSizeTextSpan.CopyTo(chunkSizeTextSpan);

                if (!Utf8Parser.TryParse(chunkSizeTextSpan, out chunkSize, out _, 'x'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseContentLength(in ReadOnlySequence<byte> remaining, out int contentLength)
        {
            if (remaining.IsSingleSegment)
            {
                if (!Utf8Parser.TryParse(remaining.FirstSpan.TrimStart((byte)' '), out contentLength, out _))
                {
                    return false;
                }
            }
            else
            {
                Span<byte> contentLengthText = stackalloc byte[128];
                remaining.CopyTo(contentLengthText);

                if (!Utf8Parser.TryParse(contentLengthText.TrimStart((byte)' '), out contentLength, out _))
                {
                    return false;
                }
            }

            return true;
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
            public long ContentLengthRemaining { get; set; }
            public bool HasContentLengthHeader { get; set; }
            public int LastChunkRemaining { get; set; }
            public int Reads { get; set; }
        }
    }
}
