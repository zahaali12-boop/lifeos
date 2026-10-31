import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";

export type PrintTemplate = components["schemas"]["PrintTemplateSummary"];
export type PrintTemplateVersion = components["schemas"]["PrintTemplateVersionSummary"];
export type PrintRender = components["schemas"]["PrintRenderSummary"];
export type PrintEmail = components["schemas"]["PrintEmailSummary"];
export type PrintShare = components["schemas"]["PrintShareSummary"];
export type PrintDocumentType = components["schemas"]["PrintDocumentTypeSummary"];

export const languageModes = ["bilingual", "en", "ar"] as const;
export const papers = ["A4", "Letter"] as const;

/** What prints, with the variants of each (the tax invoice and the pro forma of an invoice). */
export function useDocumentTypes() {
  return useQuery({ queryKey: ["print-document-types"], staleTime: 300_000, queryFn: async () => unwrap(await api.GET("/api/v1/print/document-types")) });
}

export function usePrintTemplates(documentType: string) {
  return useQuery({
    queryKey: ["print-templates", documentType],
    queryFn: async () => unwrap(await api.GET("/api/v1/print/templates", { params: { query: documentType ? { documentType } : {} } })),
  });
}

export function useBranches(companyId: string) {
  return useQuery({
    queryKey: ["branches", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/branches", { params: { path: { companyId } } })),
  });
}

export function documentTypeLabel(t: TFunction, type: string): string {
  return t(`print.documentTypes.${type}`, { defaultValue: type });
}

/** How long the dialog waits for the worker to make a PDF before giving up. */
const renderTimeoutMs = 90_000;
const renderPollMs = 1_500;

/** Polls a render until the worker has made it or it failed; the render as it stood last, whatever its status. */
export async function awaitRender(id: string): Promise<PrintRender> {
  const until = Date.now() + renderTimeoutMs;
  for (;;) {
    const render = unwrap(await api.GET("/api/v1/print/renders/{renderId}", { params: { path: { renderId: id } } }));
    if (render.status !== "queued" || Date.now() > until) {
      return render;
    }
    await new Promise((resolve) => setTimeout(resolve, renderPollMs));
  }
}

/** Keeps the digits of a phone number the way WhatsApp links want them (country code first, no plus). */
export function phoneDigits(phone: string): string {
  const digits = phone.replace(/\D/gu, "");
  return digits.startsWith("00") ? digits.slice(2) : digits;
}
