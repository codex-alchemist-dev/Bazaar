using System.Text;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>Runs one <see cref="LanguageServerCore"/> over a pair of streams with Content-Length framing.</summary>
public static class LanguageServerHost
{
    /// <summary>Serves until the stream ends, <c>exit</c> arrives or a fatal framing error happens. Returns the process exit code.</summary>
    public static async Task<int> RunAsync(Stream input, Stream output, RubedoSchema? schema = null, CancellationToken cancellationToken = default)
    {
        var core = new LanguageServerCore(schema);
        var reader = new FrameReader(input);
        while (!core.ExitRequested)
        {
            IReadOnlyList<string> replies;
            try
            {
                byte[]? body = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    break;
                }

                replies = core.Process(body);
            }
            catch (LspFramingException ex)
            {
                if (ex.Fatal)
                {
                    await WriteAsync(output, LanguageServerCore.ParseError(ex.Message), cancellationToken).ConfigureAwait(false);
                    return 1;
                }

                replies = LanguageServerCore.ParseError(ex.Message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            await WriteAsync(output, replies, cancellationToken).ConfigureAwait(false);
        }

        return core.ExitRequested ? core.ExitCode : 1;
    }

    /// <summary>Frames one message body for the wire.</summary>
    public static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        byte[] frame = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
        return frame;
    }

    private static async Task WriteAsync(Stream output, IReadOnlyList<string> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        foreach (string message in messages)
        {
            await output.WriteAsync(Frame(message), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
