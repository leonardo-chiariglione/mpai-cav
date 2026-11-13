namespace Mpai.Aif.Tests;

// The TPM simulators of the tests: AIF.RootOfTrust.TpmSimulators starts them (WSL, ~/mpai-tpm). The parties of the tests: the Controller, a host, a host whose
// code is altered, and one for the tests of the TPM alone. Each uses its port and the next. (The AIH demonstration in the Secure Profile uses 2461 and up.)
public static class TpmSimulators
{
    public const string Distribution = AIF.RootOfTrust.TpmSimulators.Distribution;
    public const string Simulator = AIF.RootOfTrust.TpmSimulators.Simulator;
    public const int Controller = 2421, Host = 2431, AlteredHost = 2441, Alone = 2451;

    public static (string Host, int Port) At(int port) => AIF.RootOfTrust.TpmSimulators.At(port);
}
