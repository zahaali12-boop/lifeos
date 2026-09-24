import { fileNameOf } from "./download";

describe("fileNameOf", () => {
  it("prefers the UTF-8 name, so an Arabic file keeps its name", () => {
    expect(fileNameOf("attachment; filename=_______-20260924.xlsx; filename*=UTF-8''%D8%A7%D9%84%D8%B4%D8%B1%D9%83%D8%A7%D8%AA-20260924.xlsx")).toBe("الشركات-20260924.xlsx");
  });

  it("falls back to the plain name, quoted or not, and to nothing without one", () => {
    expect(fileNameOf('attachment; filename="trial-balance-2026-09-30.csv"')).toBe("trial-balance-2026-09-30.csv");
    expect(fileNameOf("attachment; filename=ledger.xlsx")).toBe("ledger.xlsx");
    expect(fileNameOf("attachment; filename=plain.csv; filename*=UTF-8''%E0%A4%A")).toBe("plain.csv");
    expect(fileNameOf("")).toBeNull();
  });
});
