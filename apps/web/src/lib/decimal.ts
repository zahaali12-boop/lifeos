/**
 * Exact arithmetic on the decimal strings the API sends ("12.500000"), for the little the screens compute themselves
 * (what is left to receive, whether a line is fully covered). Never floating point: values are scaled to integers
 * (BigInt) at the larger number of decimals involved. Anything that must round goes to the server, which owns
 * RoundingPolicy.
 */
const pattern = /^([+-])?(\d*)(?:\.(\d*))?$/;

interface Scaled {
  value: bigint;
  scale: number;
}

function parse(input: string | number): Scaled | null {
  const text = typeof input === "number" ? String(input) : input.trim();
  const match = pattern.exec(text);
  if (!match || (!match[2] && !match[3])) {
    return null;
  }
  const [, sign, whole = "", fraction = ""] = match;
  const digits = `${whole || "0"}${fraction}`;
  const value = BigInt(digits);
  return { value: sign === "-" ? -value : value, scale: fraction.length };
}

function align(a: Scaled, b: Scaled): [bigint, bigint, number] {
  const scale = Math.max(a.scale, b.scale);
  return [a.value * 10n ** BigInt(scale - a.scale), b.value * 10n ** BigInt(scale - b.scale), scale];
}

function format(value: bigint, scale: number): string {
  const negative = value < 0n;
  const digits = (negative ? -value : value).toString().padStart(scale + 1, "0");
  const whole = digits.slice(0, digits.length - scale);
  const fraction = scale > 0 ? digits.slice(digits.length - scale).replace(/0+$/, "") : "";
  return `${negative ? "-" : ""}${whole}${fraction ? `.${fraction}` : ""}`;
}

/** True when the text is a plain decimal number ("12", "0.5", "-3.25"). */
export function isDecimal(input: string): boolean {
  return parse(input) !== null;
}

/** a − b − c …, exact; trailing zeros trimmed ("12.500000" − "2.5" = "10"). Invalid input counts as zero. */
export function subtract(first: string | number, ...rest: (string | number)[]): string {
  let total = parse(first) ?? { value: 0n, scale: 0 };
  for (const item of rest) {
    const [a, b, scale] = align(total, parse(item) ?? { value: 0n, scale: 0 });
    total = { value: a - b, scale };
  }
  return format(total.value, total.scale);
}

/** a + b + c …, exact. Invalid input counts as zero. */
export function add(...items: (string | number)[]): string {
  let total: Scaled = { value: 0n, scale: 0 };
  for (const item of items) {
    const [a, b, scale] = align(total, parse(item) ?? { value: 0n, scale: 0 });
    total = { value: a + b, scale };
  }
  return format(total.value, total.scale);
}

/** −1, 0 or 1 as a is less than, equal to or greater than b. Invalid input counts as zero. */
export function compare(a: string | number, b: string | number): number {
  const [x, y] = align(parse(a) ?? { value: 0n, scale: 0 }, parse(b) ?? { value: 0n, scale: 0 });
  return x === y ? 0 : x < y ? -1 : 1;
}
