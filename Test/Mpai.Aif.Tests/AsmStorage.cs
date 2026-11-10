using AIF.SharedStorage;
using Mpai.Cae.Asm;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE PRIVATE STORAGE OF A MODULE for the tests of the editing AIMs (Audio Scene Management): one store at a directory, a handle per AIM (each AIM is its own holder, as the Controller
// binds it), and the ModuleStore an editing AIM keeps what it edits in, with the Shared Storage the Module offers its Objects and Scenes in.
internal static class AsmStorage
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RuledStore> stores = new();

    // One store per directory: every call for the same directory gives the same Private Storage.
    public static RuledStore PrivateAt(string directory) => stores.GetOrAdd(directory, d => new(() => d, () => DateTimeOffset.UtcNow, everyoneReads: false, centralControl: null));

    public static IRuledStorage Handle(RuledStore store, string aim) => store.For(new StorageHolder(AsmProvider.Module, aim), "ASM-Test", "ASM-Session");

    public static ModuleStore Keep(RuledStore store, string aim, ISharedStorage? shared = null) => new(Handle(store, aim), AsmProvider.Module, shared, aim);
}
