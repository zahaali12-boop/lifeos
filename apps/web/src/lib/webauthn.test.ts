import { creationOptions, fromBase64Url, keyErrorKind, requestOptions, toBase64Url } from "./webauthn";

const bytes = (buffer: ArrayBuffer | ArrayBufferView | BufferSource): number[] => Array.from(buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : new Uint8Array((buffer as ArrayBufferView).buffer));

describe("webauthn", () => {
  it("round-trips base64url without padding, including the characters that differ from base64", () => {
    const data = new Uint8Array([0xfb, 0xff, 0xbf, 0x00, 0x01, 0x3e, 0x3f]);
    const encoded = toBase64Url(data);
    expect(encoded).toBe("-_-_AAE-Pw");
    expect(bytes(fromBase64Url(encoded))).toEqual(Array.from(data));
  });

  it("turns the server's registration options into browser options with binary challenge, user id and excluded keys", () => {
    const options = creationOptions({
      rp: { id: "localhost", name: "Quicker" },
      user: { id: "AQID", name: "owner@example.test", displayName: "Owner" },
      challenge: "BAUG",
      pubKeyCredParams: [{ type: "public-key", alg: -7 }],
      timeout: 60000,
      attestation: "none",
      authenticatorSelection: { residentKey: "preferred", userVerification: "preferred" },
      excludeCredentials: [{ type: "public-key", id: "Bwg", transports: ["usb"] }],
      status: "ok",
    });
    expect(bytes(options.challenge)).toEqual([4, 5, 6]);
    expect(bytes(options.user.id)).toEqual([1, 2, 3]);
    expect(options.rp).toEqual({ id: "localhost", name: "Quicker" });
    expect(options.excludeCredentials?.map((d) => bytes(d.id))).toEqual([[7, 8]]);
    expect(options.excludeCredentials?.[0]?.transports).toEqual(["usb"]);
    expect(options).not.toHaveProperty("status");
  });

  it("turns the server's sign-in options into browser options with the allowed keys", () => {
    const options = requestOptions({ challenge: "CQo", rpId: "localhost", userVerification: "preferred", allowCredentials: [{ type: "public-key", id: "Cw" }] });
    expect(bytes(options.challenge)).toEqual([9, 10]);
    expect(options.rpId).toBe("localhost");
    expect(options.allowCredentials?.map((d) => bytes(d.id))).toEqual([[11]]);
  });

  it("names why a key request failed", () => {
    expect(keyErrorKind(new DOMException("cancelled", "NotAllowedError"))).toBe("cancelled");
    expect(keyErrorKind(new DOMException("bad domain", "SecurityError"))).toBe("unsupported");
    expect(keyErrorKind(new Error("network"))).toBe("failed");
  });
});
