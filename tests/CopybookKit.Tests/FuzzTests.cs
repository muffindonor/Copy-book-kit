namespace CopybookKit.Tests;

/// <summary>
/// Random tests with fixed seeds, so a failure can be repeated. For a longer run, set
/// COPYBOOKKIT_FUZZ_CASES to the number of seeds and COPYBOOKKIT_FUZZ_SEED to the first one.
/// </summary>
public class FuzzTests
{
    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("COPYBOOKKIT_FUZZ_CASES"), out int n) ? n : 300;

    private static int FirstSeed => int.TryParse(Environment.GetEnvironmentVariable("COPYBOOKKIT_FUZZ_SEED"), out int n) ? n : 1;

    [Fact]
    public void Generated_copybooks_read_back_what_was_written_and_survive_damage()
    {
        for (int seed = FirstSeed; seed < FirstSeed + Cases; seed++)
        {
            Fuzzing.RunCase(seed);
        }

        // The generator must actually reach these, or the test proves less than it seems to.
        FuzzStatistics seen = Fuzzing.Statistics;
        Assert.True(seen.FieldsCompared > Cases, seen.ToString());
        Assert.True(seen.WithVariableTables > 0 && seen.WithRedefines > 0 && seen.Ascii > 0, seen.ToString());
        Assert.All(seen.Usages, count => Assert.True(count > 0, seen.ToString()));
        Assert.True(seen.DataRejected > 0 && seen.CopybooksRejected > 0 && seen.DamagedCopybooksParsed > 0, seen.ToString());
        Assert.False(Environment.GetEnvironmentVariable("COPYBOOKKIT_FUZZ_REPORT") == "1", seen.ToString());
    }

    [Fact]
    public void Damaged_sample_copybooks_fail_only_with_the_documented_errors()
    {
        string[] files = [.. Directory.GetFiles(TestData.Sample(), "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cpy", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
        Assert.NotEmpty(files);
        string[] sources = [.. files.Select(File.ReadAllText)];
        for (int seed = FirstSeed; seed < FirstSeed + (Cases * 4); seed++)
        {
            Fuzzing.RunSampleCase(sources[seed % sources.Length], seed);
        }
    }
}
