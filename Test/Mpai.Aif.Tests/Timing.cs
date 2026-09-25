namespace Mpai.Aif.Tests;

// TESTS THAT JUDGE TIME RUN ALONE. A test whose expected result depends on when
// something happens - a read within its timeout, a Message dropped past its MaxAge,
// a record played at its pace, a datum gone after its time - measures the AIF only
// when nothing else competes for the machine. Run beside the other tests, the shared
// pool of threads is taken by them (hashing models, making drives, hosts starting),
// and such a test fails at random: it measured the load, not the AIF. xUnit runs a
// collection that disables parallelisation after every parallel test has finished,
// one class at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Timing
{
    public const string Name = "Timing: run alone";
}
