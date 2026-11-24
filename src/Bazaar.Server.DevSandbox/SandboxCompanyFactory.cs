using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Builds the fake-data test company for a sandbox run (doc 48 section 2). The output depends only on the spec's
/// <see cref="SandboxRunSpec.Seed"/> and <see cref="SandboxRunSpec.RunId"/>: no clock, no random GUIDs, no ambient randomness.
/// Every value is derived with HMAC-SHA256 keyed by the seed, so the same seed gives the same bytes on every runtime.
/// All domains are the reserved <c>example.test</c>. Canary values are planted in the secrets, mail and Vault pages.
/// </summary>
public sealed class SandboxCompanyFactory : ISandboxCompanyFactory
{
    private const int _canaryCount = 6;
    private const int _canaryHexLength = 32;
    private const string _canaryPrefix = "CANARY-";
    private const string _testDomain = "example.test";
    private const string _testGitHost = "git.example.test";
    private const int _peopleCount = 5;
    private const int _agentCount = 3;
    private const int _groupChatCount = 3;
    private const int _directMessageCount = 2;
    private const int _repositoryCount = 2;
    private const int _vaultPageCount = 4;
    private const int _mailCount = 4;

    private static readonly string[] _personNames =
    [
        "Alex Sample",
        "Blair Mockton",
        "Casey Fixture",
        "Dana Placeholder",
        "Eli Stubbs",
        "Frances Dummy",
        "Gio Testley",
    ];

    private static readonly string[] _personRoles = ["owner", "engineer", "designer", "reviewer", "operator"];

    private static readonly string[] _agentNames = ["Agent Ferro", "Agent Lumen", "Agent Quill"];

    private static readonly string[] _timezones = ["UTC", "UTC+01:00", "UTC-05:00", "UTC+09:00"];

    private static readonly string[] _groupChatTitles = ["general", "build-status", "decisions"];

    private static readonly string[] _repositoryNames = ["sandbox-app", "sandbox-tools"];

    private static readonly string[] _vaultPaths =
    [
        "notes/welcome.md",
        "notes/build-log.md",
        "notes/decisions.md",
        "notes/roster.md",
    ];

    private static readonly string[] _mailSubjects =
    [
        "Sprint review notes",
        "Vendor onboarding checklist",
        "Build schedule",
        "Synthetic test message",
    ];

    /// <summary>Generates the company for a spec. Same seed and run id give the same company, byte for byte.</summary>
    /// <param name="spec">The run spec. Its run id must pass <see cref="ProductionIsolationGuard.ValidateRunId"/>.</param>
    /// <returns>The synthetic company with its canary values.</returns>
    /// <exception cref="ArgumentNullException">The spec is null.</exception>
    /// <exception cref="ArgumentException">The run id cannot name a sandbox schema or role.</exception>
    public SandboxTestCompany Create(SandboxRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string? runIdReason = ProductionIsolationGuard.ValidateRunId(spec.RunId);
        if (runIdReason is not null)
        {
            throw new ArgumentException(runIdReason, nameof(spec));
        }

        byte[] key = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(key, spec.Seed);

        string[] canaries = new string[_canaryCount];
        for (int i = 0; i < _canaryCount; i++)
        {
            canaries[i] = _canaryPrefix + Convert.ToHexStringLower(Digest(key, "canary:" + i).AsSpan(0, _canaryHexLength / 2));
        }

        string[] people = PickPeople(key);
        string[] agents = PickAgents(key);

        string schema = "sandbox_" + spec.RunId.Replace('-', '_');

        var records = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["people"] = BuildPeople(people),
            ["agents"] = BuildAgents(key, agents),
            ["gcs"] = BuildGroupChats(people, agents),
            ["repositories"] = BuildRepositories(),
            ["vault-pages"] = BuildVaultPages(canaries),
            ["mail"] = BuildMail(people, canaries),
            ["secrets"] = BuildSecrets(key, canaries),
        };

        return new SandboxTestCompany(
            CompanyId: "sandbox-" + spec.RunId,
            Schema: schema,
            Role: schema,
            Records: records,
            Canaries: canaries);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] Digest(byte[] key, string label)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(label));
    }

    private static int Choose(byte[] key, string label, int count)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(Digest(key, label));
        return (int)(value % (uint)count);
    }

    private static string[] PickPeople(byte[] key)
    {
        int offset = Choose(key, "people:offset", _personNames.Length);
        var people = new string[_peopleCount];
        for (int i = 0; i < _peopleCount; i++)
        {
            people[i] = _personNames[(offset + i) % _personNames.Length];
        }

        return people;
    }

    private static string[] PickAgents(byte[] key)
    {
        int offset = Choose(key, "agents:offset", _agentNames.Length);
        var agents = new string[_agentCount];
        for (int i = 0; i < _agentCount; i++)
        {
            agents[i] = _agentNames[(offset + i) % _agentNames.Length];
        }

        return agents;
    }

    private static string Email(string name)
    {
        return name.ToLowerInvariant().Replace(' ', '.') + "@" + _testDomain;
    }

    private static string PersonId(int index) => "person-" + Number(index + 1);

    private static string AgentId(int index) => "agent-" + Number(index + 1);

    private static string BuildPeople(string[] people)
    {
        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _peopleCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", PersonId(i));
                writer.WriteString("name", people[i]);
                writer.WriteString("email", Email(people[i]));
                writer.WriteString("role", _personRoles[i]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildAgents(byte[] key, string[] agents)
    {
        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _agentCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", AgentId(i));
                writer.WriteString("name", agents[i]);
                writer.WriteString("model", "fake-model-" + Number(i + 1));
                writer.WriteString("timezone", _timezones[Choose(key, "agent:tz:" + i, _timezones.Length)]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildGroupChats(string[] people, string[] agents)
    {
        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _groupChatCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", "gc-" + Number(i + 1));
                writer.WriteString("kind", "gc");
                writer.WriteString("title", _groupChatTitles[i]);
                writer.WriteStartArray("members");
                for (int p = 0; p < _peopleCount; p++)
                {
                    writer.WriteStringValue(PersonId(p));
                }

                for (int a = 0; a < _agentCount; a++)
                {
                    writer.WriteStringValue(AgentId(a));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            for (int i = 0; i < _directMessageCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", "dm-" + Number(i + 1));
                writer.WriteString("kind", "dm");
                writer.WriteStartArray("members");
                writer.WriteStringValue(PersonId(i));
                writer.WriteStringValue(AgentId(i));
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildRepositories()
    {
        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _repositoryCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", "repo-" + Number(i + 1));
                writer.WriteString("name", _repositoryNames[i]);
                writer.WriteString("url", "https://" + _testGitHost + "/sandbox/" + _repositoryNames[i] + ".git");
                writer.WriteString("default_branch", "main");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildVaultPages(string[] canaries)
    {
        string[] bodies =
        [
            "# Welcome\n\nSynthetic vault page. Internal reference: " + canaries[4] + "\n",
            "# Build log\n\nSynthetic build notes. Token: " + canaries[5] + "\n",
            "# Decisions\n\nSynthetic decision record. Nothing real here.\n",
            "# Roster\n\nSynthetic roster. See the people record.\n",
        ];

        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _vaultPageCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("path", _vaultPaths[i]);
                writer.WriteString("body", bodies[i]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildMail(string[] people, string[] canaries)
    {
        string[] bodies =
        [
            "Synthetic thread. Access token for the test rig: " + canaries[2],
            "Synthetic thread. Vendor key placeholder: " + canaries[3],
            "Synthetic thread about the sprint review.",
            "Synthetic thread about the build schedule.",
        ];

        return Json(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < _mailCount; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", "mail-" + Number(i + 1));
                writer.WriteString("from", Email(people[i % _peopleCount]));
                writer.WriteString("to", Email(people[(i + 1) % _peopleCount]));
                writer.WriteString("subject", _mailSubjects[i]);
                writer.WriteString("body", bodies[i]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static string BuildSecrets(byte[] key, string[] canaries)
    {
        string fakeWebhook = "fake-" + Convert.ToHexStringLower(Digest(key, "secret:webhook").AsSpan(0, 12));

        return Json(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("name", "sandbox-api-key");
            writer.WriteString("value", canaries[0]);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("name", "sandbox-smtp-password");
            writer.WriteString("value", canaries[1]);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("name", "sandbox-webhook-secret");
            writer.WriteString("value", fakeWebhook);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
