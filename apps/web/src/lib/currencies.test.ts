import { describe, expect, it } from "vitest";
import { currencyOptions, defaultFavouriteCurrencies, parseFavourites, type Currency } from "./currencies";

const currency = (code: string, en: string, ar: string, isActive = true): Currency => ({ code, numericCode: "000", minorUnits: 2, symbol: code, name: { en, ar }, isActive });
const all = [
  currency("EUR", "Euro", "يورو"),
  currency("USD", "US dollar", "دولار أمريكي"),
  currency("AED", "UAE dirham", "درهم إماراتي"),
  currency("IQD", "Iraqi dinar", "دينار عراقي"),
  currency("JOD", "Jordanian dinar", "دينار أردني"),
  currency("ZWL", "Zimbabwe dollar", "دولار زيمبابوي", false),
];
const codes = (list: Currency[]) => list.map((c) => c.code);

describe("currency picker options", () => {
  it("pins the favourites in their order, then every other active currency by code", () => {
    const options = currencyOptions(all, ["IQD", "USD", "AED"], "");
    expect(codes(options.favourites)).toEqual(["IQD", "USD", "AED"]);
    expect(codes(options.others)).toEqual(["EUR", "JOD"]);
  });

  it("searches by code prefix or by the name in English or Arabic, favourites first", () => {
    expect(codes(currencyOptions(all, ["IQD", "USD", "AED"], "dinar").favourites)).toEqual(["IQD"]);
    expect(codes(currencyOptions(all, ["IQD", "USD", "AED"], "dinar").others)).toEqual(["JOD"]);
    expect(codes(currencyOptions(all, ["IQD", "USD", "AED"], "دولار").favourites)).toEqual(["USD"]);
    expect(codes(currencyOptions(all, ["IQD", "USD", "AED"], "eu").others)).toEqual(["EUR"]);
  });

  it("never offers an inactive currency, even as a favourite", () => {
    const options = currencyOptions(all, ["ZWL", "IQD"], "");
    expect(codes(options.favourites)).toEqual(["IQD"]);
    expect(codes(options.others)).not.toContain("ZWL");
  });
});

describe("favourite currencies setting", () => {
  it("defaults to IQD, USD and AED when unset or unusable", () => {
    expect(defaultFavouriteCurrencies).toEqual(["IQD", "USD", "AED"]);
    expect(parseFavourites(undefined)).toEqual(defaultFavouriteCurrencies);
    expect(parseFavourites("IQD")).toEqual(defaultFavouriteCurrencies);
    expect(parseFavourites([])).toEqual(defaultFavouriteCurrencies);
  });

  it("keeps the administrator's order and drops anything that is not a code", () => {
    expect(parseFavourites(["USD", "eur", 7, "IQD", "USD"])).toEqual(["USD", "IQD"]);
  });
});
