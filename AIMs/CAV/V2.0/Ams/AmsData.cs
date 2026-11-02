namespace Mpai.Cav.Ams;

// THE AMS DATA, in the Shared Storage of the Module (the author, 2026/11/01). What the AMS decides and what is worth recording for
// the future: written and read by every AMS AIM, and readable by the parties the Shared Storage authorises. An AIM's own working data
// - what it needs between two steps and nobody else does - is kept in its Private Storage, never here.
public static class AmsData
{
    public const string Prefix = "ams-data/";
}
