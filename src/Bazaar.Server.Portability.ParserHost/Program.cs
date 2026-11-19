using Bazaar.Server.Portability;
using Bazaar.Server.Portability.ParserHost;
using Bazaar.Server.Portability.Sandbox;

// Parser child process (doc 44 section 8.3). Reads one request from stdin, runs Detect or Parse of one importer,
// and writes length-prefixed JSON frames to stdout. It writes nothing else to the instance and opens no sockets.
// Exit codes: 0 finished (done or error frame sent), 2 bad request or protocol, anything else is a crash.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
// Importer code must not corrupt the frame stream: anything it prints goes to stderr.
Console.SetOut(Console.Error);

var extra = new List<string>();
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--importer-assembly") extra.Add(args[++i]);
}

ParserRequest? request;
try
{
    request = await ParserFrames.ReadRequestAsync(stdin, 64 * 1024, CancellationToken.None);
}
catch (InvalidDataException ex)
{
    return BadRequest(stdout, "bad request: " + ex.Message);
}

if (request is null || request.Protocol != ParserRequest.CurrentProtocol || request.ContentLength < 0)
{
    return BadRequest(stdout, "bad request header");
}

ImportLimits limits;
try
{
    limits = request.ToLimits();
}
catch (ArgumentOutOfRangeException ex)
{
    return BadRequest(stdout, "bad limits: " + ex.Message);
}

try
{
    // The child enforces the source-size limit before it reads or allocates anything.
    if (request.ContentLength > limits.MaxSourceBytes || request.ContentLength > Array.MaxLength)
    {
        Fail(stdout, ParserErrorKinds.Limit, $"source is {request.ContentLength} bytes, over the limit of {limits.MaxSourceBytes}");
        return 0;
    }

    var catalog = new ImporterCatalog(extra);

    if (request.Op == ParserOps.List)
    {
        var list = catalog.List(limits).Select(i => new ParserImporterInfo(i.Id, i.Version)).ToList();
        ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Importers, Importers = list });
        ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Done, Count = list.Count });
        return 0;
    }

    var importer = catalog.Find(request.ImporterId, limits);
    if (importer is null)
    {
        Fail(stdout, ParserErrorKinds.Parser, "importer is not known to the parser host: " + request.ImporterId);
        return 0;
    }

    var content = new byte[request.ContentLength];
    var read = 0;
    while (read < content.Length)
    {
        var n = stdin.Read(content, read, content.Length - read);
        if (n == 0) break;
        read += n;
    }
    if (read < content.Length)
    {
        return BadRequest(stdout, "input ended early");
    }

    var source = new ImportSource(request.SourceName, content);
    switch (request.Op)
    {
        case ParserOps.Detect:
            var detected = importer.Detect(source);
            ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.DetectResult, Confidence = detected.Confidence, Reason = detected.Reason });
            ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Done, Count = 1 });
            return 0;
        case ParserOps.Parse:
            var records = importer.Parse(source);
            var count = 0;
            foreach (var record in records)
            {
                var bytes = ParserFrames.RecordBytes(record);
                if (bytes > limits.MaxRecordBytes)
                {
                    Fail(stdout, ParserErrorKinds.Limit, $"a record is {bytes} bytes, over the limit of {limits.MaxRecordBytes}");
                    return 0;
                }
                ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Record, Record = record });
                count++;
            }
            ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Done, Count = count });
            return 0;
        default:
            return BadRequest(stdout, "unknown op: " + request.Op);
    }
}
catch (ImportLimitException ex)
{
    Fail(stdout, ParserErrorKinds.Limit, ex.Message);
    return 0;
}
catch (OutOfMemoryException)
{
    Fail(stdout, ParserErrorKinds.Memory, "the parser ran out of memory (memory ceiling)");
    return 0;
}
catch (Exception ex)
{
    Fail(stdout, ParserErrorKinds.Parser, ex.GetType().Name + ": " + ex.Message);
    return 0;
}

// A rejected request header is reported as a structured frame, then exit code 2.
static int BadRequest(Stream stdout, string message)
{
    ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Error, ErrorKind = ParserErrorKinds.BadRequest, Message = message });
    return 2;
}

static void Fail(Stream stdout, string kind, string message) =>
    ParserFrames.Write(stdout, new ParserMessage { Type = ParserMessageTypes.Error, ErrorKind = kind, Message = message });
