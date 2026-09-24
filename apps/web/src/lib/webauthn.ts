/**
 * The browser side of security keys and passkeys (WebAuthn). The server sends its options as JSON with binary fields
 * in base64url; the browser API takes and returns ArrayBuffers; the server reads the result back as JSON in base64url.
 * Written out rather than using PublicKeyCredential.parseCreationOptionsFromJSON, which older browsers lack.
 */

type Json = Record<string, unknown>;

export function toBase64Url(buffer: ArrayBuffer | ArrayBufferView): string {
  const bytes = buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : new Uint8Array(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromBase64Url(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

/** Whether this browser can use security keys at all (it needs a secure context: https, or localhost). */
export function webAuthnAvailable(): boolean {
  return typeof window !== "undefined" && window.isSecureContext && typeof window.PublicKeyCredential === "function" && typeof navigator.credentials.create === "function";
}

function descriptors(list: unknown): PublicKeyCredentialDescriptor[] | undefined {
  return Array.isArray(list)
    ? (list as { id: string; type?: string; transports?: AuthenticatorTransport[] }[]).map((d) => ({ type: "public-key", id: fromBase64Url(d.id), ...(d.transports && d.transports.length > 0 ? { transports: d.transports } : {}) }))
    : undefined;
}

/** Registration options from the server, ready for navigator.credentials.create. */
export function creationOptions(options: Json): PublicKeyCredentialCreationOptions {
  const user = options.user as { id: string; name: string; displayName: string };
  const selection = options.authenticatorSelection as AuthenticatorSelectionCriteria | undefined;
  return {
    rp: options.rp as PublicKeyCredentialRpEntity,
    user: { id: fromBase64Url(user.id), name: user.name, displayName: user.displayName },
    challenge: fromBase64Url(options.challenge as string),
    pubKeyCredParams: options.pubKeyCredParams as PublicKeyCredentialParameters[],
    ...(typeof options.timeout === "number" ? { timeout: options.timeout } : {}),
    ...(typeof options.attestation === "string" ? { attestation: options.attestation as AttestationConveyancePreference } : {}),
    ...(selection ? { authenticatorSelection: selection } : {}),
    excludeCredentials: descriptors(options.excludeCredentials) ?? [],
  };
}

/** Sign-in options from the server, ready for navigator.credentials.get. */
export function requestOptions(options: Json): PublicKeyCredentialRequestOptions {
  return {
    challenge: fromBase64Url(options.challenge as string),
    ...(typeof options.timeout === "number" ? { timeout: options.timeout } : {}),
    ...(typeof options.rpId === "string" ? { rpId: options.rpId } : {}),
    ...(typeof options.userVerification === "string" ? { userVerification: options.userVerification as UserVerificationRequirement } : {}),
    allowCredentials: descriptors(options.allowCredentials) ?? [],
  };
}

/** Creates a credential on the person's key or device and returns it in the shape the server verifies. */
export async function registerKey(options: Json): Promise<Json> {
  const credential = (await navigator.credentials.create({ publicKey: creationOptions(options) })) as PublicKeyCredential | null;
  if (!credential) {
    throw new DOMException("No credential was created.", "NotAllowedError");
  }
  const response = credential.response as AuthenticatorAttestationResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      attestationObject: toBase64Url(response.attestationObject),
      clientDataJSON: toBase64Url(response.clientDataJSON),
      ...(typeof response.getTransports === "function" ? { transports: response.getTransports() } : {}),
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}

/** Asks the person's key or device to sign the server's challenge and returns the assertion the server verifies. */
export async function signWithKey(options: Json): Promise<Json> {
  const credential = (await navigator.credentials.get({ publicKey: requestOptions(options) })) as PublicKeyCredential | null;
  if (!credential) {
    throw new DOMException("No credential was used.", "NotAllowedError");
  }
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      authenticatorData: toBase64Url(response.authenticatorData),
      clientDataJSON: toBase64Url(response.clientDataJSON),
      signature: toBase64Url(response.signature),
      ...(response.userHandle ? { userHandle: toBase64Url(response.userHandle) } : {}),
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}

/** A browser refusal (cancelled, timed out, key not registered here) as a key into `account.keyErrors`. */
export function keyErrorKind(error: unknown): "cancelled" | "unsupported" | "failed" {
  if (error instanceof DOMException) {
    if (error.name === "NotAllowedError" || error.name === "AbortError") {
      return "cancelled";
    }
    if (error.name === "SecurityError" || error.name === "NotSupportedError") {
      return "unsupported";
    }
  }
  return "failed";
}
