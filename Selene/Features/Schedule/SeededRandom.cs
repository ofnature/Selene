namespace Selene.Features.Schedule;

/// <summary>
/// The dice of one character's play day (SplitMix64). The same character and day always roll the same
/// numbers - on every PC, in every process, after every plugin reload - so a plan survives a reload
/// mid-evening and needs no file. That rules out <c>HashCode</c> (seeded per process) for the seed.
/// </summary>
internal struct SeededRandom
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    /// <summary>Both halves are mixed, so neighbouring days (and neighbouring ids) share nothing.</summary>
    public SeededRandom(ulong contentId, int dayNumber)
    {
        _state = Mix(contentId ^ Mix(unchecked((ulong)dayNumber + Gamma)));
    }

    /// <summary>The next number in [0, 1).</summary>
    public double NextDouble()
    {
        unchecked
        {
            _state += Gamma;
        }

        return (Mix(_state) >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
