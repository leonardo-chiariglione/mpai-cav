// THE BROWSER'S OWN ECDSA FOR BOUTS (MPAI-AIF V3.0, Communication 9.1; MPAI-MAS V1.1, API 3.3): ECDSA P-256 with SHA-256 by Web Crypto
// (crypto.subtle), which .NET in WebAssembly does not have. A signature is r and s, 32 bytes each (IEEE P1363) - the form Web Crypto gives and the one
// MPAI-PTF uses. The private key never leaves the browser: it is made not extractable, and the page refers to a key by a handle.
window.mpaiBout = (() => {
  const keys = new Map();
  let next = 1;
  const curve = { name: 'ECDSA', namedCurve: 'P-256' };
  const algorithm = { name: 'ECDSA', hash: 'SHA-256' };
  const keep = key => { const handle = next++; keys.set(handle, key); return handle; };
  const of = handle => { const key = keys.get(handle); if (!key) throw new Error('no such key'); return key; };
  return {
    // A new key pair; its handle.
    async generate() { return keep(await crypto.subtle.generateKey(curve, false, ['sign', 'verify'])); },
    // The SubjectPublicKeyInfo of a key.
    async spki(handle) {
      const key = of(handle);
      return new Uint8Array(await crypto.subtle.exportKey('spki', key.publicKey || key));
    },
    // A public key, from its SubjectPublicKeyInfo; its handle, or 0 where it is not a P-256 key.
    async importSpki(spki) {
      try { return keep(await crypto.subtle.importKey('spki', spki, curve, true, ['verify'])); }
      catch { return 0; }
    },
    async sign(handle, data) { return new Uint8Array(await crypto.subtle.sign(algorithm, of(handle).privateKey, data)); },
    async verify(handle, data, signature) {
      const key = of(handle);
      if (signature.length !== 64) return false;
      return crypto.subtle.verify(algorithm, key.publicKey || key, signature, data);
    },
    release(handle) { keys.delete(handle); }
  };
})();
