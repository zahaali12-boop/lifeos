import { Button } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Paperclip, Trash2 } from "lucide-react";
import { useId, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatDateTime, formatNumber } from "../lib/format";
import { toFormProblem } from "../lib/problem";

function size(bytes: number | string): string {
  const value = Number(bytes);
  return value >= 1_048_576 ? `${formatNumber(value / 1_048_576, { maximumFractionDigits: 1 })} MB` : `${formatNumber(Math.max(1, Math.round(value / 1024)))} KB`;
}

/**
 * Files attached to any record (supporting documents on a journal, a supplier's invoice scan): list, upload, download
 * and remove, through the collaboration attachments API. Bytes live in the object store; the list shows name, size and
 * when it was added. Removal leaves an audit trail on the server.
 */
export function AttachmentsPanel({ entityType, entityId, readOnly = false }: { entityType: string; entityId: string; readOnly?: boolean }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const inputId = useId();
  const [problem, setProblem] = useState<string | null>(null);
  const key = ["attachments", entityType, entityId];
  const attachments = useQuery({ queryKey: key, queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/attachments", { params: { query: { entityType, entityId } } })) });
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed")).message); };
  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData();
      form.append("entityType", entityType);
      form.append("entityId", entityId);
      form.append("file", file);
      return unwrap(await api.POST("/api/v1/collaboration/attachments", { body: { entityType, entityId, file: "" }, bodySerializer: () => form }));
    },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: key }); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: async (attachmentId: string) => { await api.DELETE("/api/v1/collaboration/attachments/{attachmentId}", { params: { path: { attachmentId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: key }); },
    onError: fail,
  });
  const download = useMutation({
    mutationFn: async (attachment: { id: string; fileName: string }) => {
      const blob = unwrap(await api.GET("/api/v1/collaboration/attachments/{attachmentId}/content", { params: { path: { attachmentId: attachment.id } }, parseAs: "blob" }));
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = attachment.fileName;
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    },
    onError: fail,
  });

  return (
    <section className="flex flex-col gap-2" aria-labelledby={`${inputId}-title`} data-testid="attachments">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={`${inputId}-title`} className="flex items-center gap-1.5 text-sm font-semibold">
          <Paperclip className="size-4" aria-hidden="true" />
          {t("attachments.title", { count: attachments.data?.length ?? 0 })}
        </h3>
        {readOnly ? null : (
          <label className="ms-auto inline-flex cursor-pointer items-center rounded-md border border-border px-2.5 py-1 text-sm hover:bg-surface-sunken focus-within:ring-2 focus-within:ring-accent">
            <input
              type="file"
              className="sr-only"
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (file) {
                  upload.mutate(file);
                }
                e.target.value = "";
              }}
              data-testid="attachment-input"
            />
            {upload.isPending ? t("attachments.uploading") : t("attachments.add")}
          </label>
        )}
      </div>
      {problem ? <p role="alert" className="text-sm text-danger">{problem}</p> : null}
      {(attachments.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("attachments.none")}</p> : (
        <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
          {(attachments.data ?? []).map((a) => (
            <li key={a.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm" data-testid="attachment-row">
              <span className="min-w-0 flex-1 truncate" dir="auto">{a.fileName}</span>
              <span className="text-xs text-fg-muted">{size(a.sizeBytes)} · {formatDateTime(a.createdAt)}</span>
              <Button variant="ghost" size="icon" aria-label={t("attachments.download", { name: a.fileName })} onClick={() => { download.mutate({ id: a.id, fileName: a.fileName }); }}>
                <Download aria-hidden="true" />
              </Button>
              {readOnly ? null : (
                <Button variant="ghost" size="icon" aria-label={t("attachments.remove", { name: a.fileName })} onClick={() => { remove.mutate(a.id); }} data-testid="remove-attachment">
                  <Trash2 aria-hidden="true" />
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
