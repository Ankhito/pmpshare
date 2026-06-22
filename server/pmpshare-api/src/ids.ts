export function createTransferId(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

export function isValidTransferId(value: string): boolean {
  return /^[a-f0-9]{32}$/.test(value);
}
