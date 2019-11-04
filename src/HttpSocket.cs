using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace HttpSocket
{
    class Program
    {
        private static ReadOnlySpan<byte> Http11 => new byte[] { (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'1', (byte)'.', (byte)'1' };
        private static ReadOnlySpan<byte> NewLine => new byte[] { (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> ContentLength => new byte[] { (byte)'C', (byte)'o', (byte)'n', (byte)'t', (byte)'e', (byte)'n', (byte)'t', (byte)'-', (byte)'L', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h' };

        static async Task Main(string[] args)
        {
            // var serverUrl = "http://10.197.175.74:5001"; // new Uri(Environment.GetEnvironmentVariable("SERVER_URL"));
            // http://10.0.0.102:5000/plaintext

            // TODO: parse server url
            // string hostName = "10.197.175.74";
            string hostName = "127.0.0.1";
            int hostPort = 5000;

            var request = $"GET / HTTP/1.1\r\n" +
                $"Host: {hostName}:{hostPort}\r\n" +
                "Content-Length: 0\r\n" +
                "\r\n";

            var requestBytes = Encoding.UTF8.GetBytes(request).AsMemory();

            IPAddress hostAddress = IPAddress.Parse(hostName);
            IPEndPoint hostEndPoint = new IPEndPoint(hostAddress, hostPort);

            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(hostEndPoint);

            var pipe = new Pipe();
            Task writing = FillPipeAsync(socket, pipe.Writer);
            var reader = pipe.Reader;

            try
            {
                var httpResponse = new HttpResponse();

                for (var k = 0; k < 100000; k++)
                {
                    httpResponse.Reset();

                    var response = await socket.SendAsync(requestBytes, SocketFlags.None);

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

                    // Console.WriteLine($"Result: {httpResponse.State}, Status: {httpResponse.StatusCode}, Content-Length: {httpResponse.ContentLength}");

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

        static void ParseHttpResponse(ref ReadOnlySequence<byte> buffer, HttpResponse httpResponse, out SequencePosition examined)
        {
            var sequenceReader = new SequenceReader<byte>(buffer);
            examined = buffer.End;

            switch (httpResponse.State)
            {
                case HttpResponseState.StartLine:
                    if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> startLine, NewLine))
                    {
                        return;
                    }

                    var space = startLine.IndexOf((byte)' ');

                    if (space == -1)
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    var version = startLine.Slice(0, space);

                    if (!version.SequenceEqual(Http11))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }

                    startLine = startLine.Slice(space + 1);

                    space = startLine.IndexOf((byte)' ');

                    if (space == -1 || !Utf8Parser.TryParse(startLine.Slice(0, space), out int statusCode, out _))
                    {
                        httpResponse.State = HttpResponseState.Error;
                        return;
                    }
                    else
                    {
                        httpResponse.StatusCode = statusCode;
                    }

                    // reason phrase
                    // startLine.Slice(space + 1)

                    httpResponse.State = HttpResponseState.Headers;

                    examined = sequenceReader.Position;

                    goto case HttpResponseState.Headers;

                case HttpResponseState.Headers:

                    // Read evey headers
                    while (sequenceReader.TryReadTo(out ReadOnlySpan<byte> headerLine, NewLine))
                    {
                        // Is that the end of the headers?
                        if (headerLine.Length == 0)
                        {
                            examined = sequenceReader.Position;

                            // End of headers

                            if (httpResponse.HasContentLengthHeader)
                            {
                                httpResponse.State = HttpResponseState.Body;

                                goto case HttpResponseState.Body;
                            }

                            httpResponse.State = HttpResponseState.ChunkedBody;

                            goto case HttpResponseState.ChunkedBody;
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
                            else if (!TryParseCrlf(ref sequenceReader, httpResponse))
                            {
                                break;
                            }

                            examined = sequenceReader.Position;
                        }
                        else
                        {
                            if (!sequenceReader.TryReadTo(out ReadOnlySpan<byte> chunkSizeText, NewLine))
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
                                if (!TryParseCrlf(ref sequenceReader, httpResponse))
                                {
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

        private static bool TryParseCrlf(ref SequenceReader<byte> sequenceReader, HttpResponse httpResponse)
        {
            // Need at least 2 characters in the buffer to make a call
            if (sequenceReader.Remaining < 2)
            {
                return false;
            }

            // We expect a crlf
            if (sequenceReader.IsNext(NewLine, advancePast: true))
            {
                return true;
            }

            // Didn't see that, broken server
            httpResponse.State = HttpResponseState.Error;
            return false;
        }

        private static void ParseHeader(in ReadOnlySpan<byte> headerLine, HttpResponse httpResponse)
        {
            var headerSpan = headerLine;
            var colon = headerSpan.IndexOf((byte)':');

            if (colon == -1)
            {
                httpResponse.State = HttpResponseState.Error;
                return;
            }

            if (!headerSpan.Slice(0, colon).SequenceEqual(ContentLength))
            {
                return;
            }

            httpResponse.HasContentLengthHeader = true;

            var value = headerSpan.Slice(colon + 1).Trim((byte)' ');

            if (Utf8Parser.TryParse(value, out long contentLength, out _))
            {
                httpResponse.ContentLength = contentLength;
                httpResponse.ContentLengthRemaining = contentLength;
            }
            else
            {
                httpResponse.State = HttpResponseState.Error;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseChunkPrefix(in ReadOnlySpan<byte> chunkSizeText, out int chunkSize)
        {
            return Utf8Parser.TryParse(chunkSizeText, out chunkSize, out _, 'x');
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
            public long ContentLength { get; set; }
            public long ContentLengthRemaining { get; set; }
            public bool HasContentLengthHeader { get; set; }
            public int LastChunkRemaining { get; set; }

            public void Reset()
            {
                State = HttpResponseState.StartLine;
                StatusCode = default;
                ContentLength = default;
                ContentLengthRemaining = default;
                HasContentLengthHeader = default;
                LastChunkRemaining = default;
            }
        }
    }
}
