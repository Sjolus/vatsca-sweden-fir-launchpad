// Software authenticator used only with disposable synthetic databases.
import { createHash, generateKeyPairSync, randomBytes, sign } from 'node:crypto';
import { isoCBOR } from '@simplewebauthn/server/helpers';
const hash = value => createHash('sha256').update(value).digest();
const encode = value => Buffer.from(value).toString('base64url');
export function authenticator() {
  const keys = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const jwk = keys.publicKey.export({ format: 'jwk' }), id = randomBytes(32);
  const cose = isoCBOR.encode(new Map([[1, 2], [3, -7], [-1, 1], [-2, new Uint8Array(Buffer.from(jwk.x, 'base64url'))], [-3, new Uint8Array(Buffer.from(jwk.y, 'base64url'))]]));
  let count = 0;
  function data(options, origin, register, overrides) {
    const client = Buffer.from(JSON.stringify({ type: register ? 'webauthn.create' : 'webauthn.get', challenge: options.challenge, origin, crossOrigin: false, ...overrides.client }));
    const counter = Buffer.alloc(4); counter.writeUInt32BE(overrides.counter ?? (register ? 0 : ++count));
    const auth = Buffer.concat([hash(overrides.rpID ?? (register ? options.rp.id : options.rpId)), Buffer.from([(overrides.uv === false ? 1 : 5) | (register ? 64 : 0)]), counter]);
    return { client, auth };
  }
  return {
    id: encode(id),
    registration(options, origin, overrides = {}) {
      const { client, auth } = data(options, origin, true, overrides), length = Buffer.alloc(2); length.writeUInt16BE(id.length);
      const attestation = isoCBOR.encode(new Map([['fmt', 'none'], ['attStmt', new Map()], ['authData', new Uint8Array(Buffer.concat([auth, Buffer.alloc(16), length, id, cose]))]]));
      return { id: encode(id), rawId: encode(id), type: 'public-key', clientExtensionResults: { credProps: { rk: true } }, response: { clientDataJSON: encode(client), attestationObject: encode(attestation), transports: ['internal'] } };
    },
    authentication(options, origin, handle, overrides = {}) {
      const { client, auth } = data(options, origin, false, overrides), signature = sign('sha256', Buffer.concat([auth, hash(client)]), keys.privateKey);
      if (overrides.badSignature) signature[0] ^= 1;
      return { id: encode(id), rawId: encode(id), type: 'public-key', clientExtensionResults: {}, response: { clientDataJSON: encode(client), authenticatorData: encode(auth), signature: encode(signature), userHandle: encode(handle) } };
    },
  };
}
