using Bazaar.Server.Portability;
using static Bazaar.Server.Portability.Postgres.Tests.ImportStoreFixtures;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>The input checks every store shares (job, batch and item rules). Pure, no database.</summary>
public class ImportStoreRulesTests
{
    [Fact]
    public void A_valid_job_is_accepted()
    {
        ImportStoreRules.ValidateNewJob(NewJob("job-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_job_id_is_refused(string jobId)
    {
        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateNewJob(NewJob(jobId)));
    }

    [Fact]
    public void A_job_id_over_128_characters_is_refused()
    {
        var longId = new string('j', ImportStoreRules.MaxJobIdLength + 1);

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateJobId(longId));
        ImportStoreRules.ValidateJobId(new string('j', ImportStoreRules.MaxJobIdLength));
    }

    [Fact]
    public void A_job_needs_a_manifest_file()
    {
        var empty = NewJob("job-1") with { SourceManifest = [] };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateNewJob(empty));
    }

    [Fact]
    public void A_manifest_digest_must_be_lowercase_hex()
    {
        var upperDigest = NewJob("job-1") with { SourceManifest = [new ImportManifestFile("a.md", 1, new string('A', 64))] };
        var shortDigest = NewJob("job-1") with { SourceManifest = [new ImportManifestFile("a.md", 1, new string('a', 63))] };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateNewJob(upperDigest));
        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateNewJob(shortDigest));
    }

    [Fact]
    public void A_negative_file_size_is_refused()
    {
        var negative = NewJob("job-1") with { SourceManifest = [new ImportManifestFile("a.md", -1, Sha('a'))] };

        Assert.Throws<ArgumentOutOfRangeException>(() => ImportStoreRules.ValidateNewJob(negative));
    }

    [Fact]
    public void A_manifest_over_the_upload_limit_is_refused()
    {
        var files = new[] { new ImportManifestFile("a.md", 600, Sha('a')), new ImportManifestFile("b.md", 600, Sha('b')) };
        var over = NewJob("job-1", maxUploadBytes: 1000) with { SourceManifest = files };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateNewJob(over));
    }

    [Fact]
    public void A_zero_upload_limit_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImportStoreRules.ValidateNewJob(NewJob("job-1", maxUploadBytes: 0)));
    }

    [Fact]
    public void A_valid_batch_is_accepted()
    {
        var items = new[] { Item("job-1", "a"), Item("job-1", "b", ImportTargetKind.VaultFile) };

        ImportStoreRules.ValidateBatch(Batch("job-1", 1, T0, items), items);
    }

    [Fact]
    public void A_batch_whose_counts_do_not_match_its_items_is_refused()
    {
        var items = new[] { Item("job-1", "a"), Item("job-1", "b") };
        var batch = Batch("job-1", 1, T0, items) with
        {
            ItemCounts = new Dictionary<ImportTargetKind, int> { [ImportTargetKind.GcMessage] = 3 },
        };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateBatch(batch, items));
    }

    [Fact]
    public void A_batch_whose_key_bounds_are_not_its_first_and_last_item_is_refused()
    {
        var items = new[] { Item("job-1", "a"), Item("job-1", "b") };
        var batch = Batch("job-1", 1, T0, items) with { LastSourceKey = "a" };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateBatch(batch, items));
    }

    [Fact]
    public void An_item_from_another_job_is_refused()
    {
        var items = new[] { Item("job-1", "a"), Item("job-2", "b") };
        var batch = Batch("job-1", 1, T0, items[0]) with { LastSourceKey = "b", ItemCounts = new Dictionary<ImportTargetKind, int> { [ImportTargetKind.GcMessage] = 2 } };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateBatch(batch, items));
    }

    [Fact]
    public void Two_items_with_one_identity_in_a_batch_are_refused()
    {
        var items = new[] { Item("job-1", "a"), Item("job-1", "a") };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateBatch(Batch("job-1", 1, T0, items), items));
    }

    [Fact]
    public void A_batch_number_below_one_is_refused()
    {
        var items = new[] { Item("job-1", "a") };

        Assert.Throws<ArgumentOutOfRangeException>(() => ImportStoreRules.ValidateBatch(Batch("job-1", 0, T0, items), items));
    }

    [Fact]
    public void A_batch_hash_must_be_a_digest()
    {
        var items = new[] { Item("job-1", "a") };
        var batch = Batch("job-1", 1, T0, items) with { BatchHash = "not-a-digest" };

        Assert.Throws<ArgumentException>(() => ImportStoreRules.ValidateBatch(batch, items));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(63, false)]
    [InlineData(65, false)]
    public void Digests_are_exactly_64_lowercase_hex_characters(int length, bool valid)
    {
        var value = new string('e', length);

        Assert.Equal(valid, ImportStoreRules.IsSha256Hex(value));
    }

    [Fact]
    public void Uppercase_hex_is_not_a_stored_digest()
    {
        Assert.False(ImportStoreRules.IsSha256Hex(new string('E', 64)));
    }
}
