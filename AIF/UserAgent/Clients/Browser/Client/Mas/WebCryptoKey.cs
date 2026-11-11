using System;
using System.Threading.Tasks;
using AIF.Bout;
using Microsoft.JSInterop;

namespace Mpai.RcaWeb.Mas;

// THE BROWSER'S OWN ECDSA AS A KEY OF THE BOUT LIBRARY. .NET in WebAssembly has no ECDSA; the browser has, in Web Crypto (wwwroot/js/bout.js). It answers
// only asynchronously, so the Bout library is used with its Async methods; the synchronous ones refuse this key. The private key is the browser's and is
// not extractable.
public sealed class WebCryptoKey : BoutKey
{
    private readonly IJSRuntime js;
    private readonly int handle;
    private readonly byte[] spki;

    private WebCryptoKey(IJSRuntime js, int handle, byte[] spki)
    {
        this.js = js;
        this.handle = handle;
        this.spki = spki;
    }

    // A new key pair, made by the browser.
    public static async Task<BoutKey> CreateAsync(IJSRuntime js)
    {
        var handle = await js.InvokeAsync<int>("mpaiBout.generate");
        return new WebCryptoKey(js, handle, await js.InvokeAsync<byte[]>("mpaiBout.spki", handle));
    }

    // A public key, from its SubjectPublicKeyInfo; null where it is not a P-256 key.
    public static async Task<BoutKey?> FromSpkiAsync(IJSRuntime js, byte[] spki)
    {
        var handle = await js.InvokeAsync<int>("mpaiBout.importSpki", spki);
        return handle == 0 ? null : new WebCryptoKey(js, handle, spki);
    }

    public override byte[] Sign(byte[] data) => throw new InvalidOperationException("The browser's key signs asynchronously: use SignAsync.");
    public override bool Verify(byte[] data, byte[] signature) => throw new InvalidOperationException("The browser's key verifies asynchronously: use VerifyAsync.");
    public override byte[] Spki() => spki;

    public override async ValueTask<byte[]> SignAsync(byte[] data) => await js.InvokeAsync<byte[]>("mpaiBout.sign", handle, data);
    public override async ValueTask<bool> VerifyAsync(byte[] data, byte[] signature) => await js.InvokeAsync<bool>("mpaiBout.verify", handle, data, signature);
}
