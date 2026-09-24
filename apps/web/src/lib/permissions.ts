import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { api, unwrap } from "../api";

/**
 * Whether the signed-in user holds a permission, from /me. For showing or hiding actions only: the API decides, so a
 * button shown in error is refused there.
 */
export function useCan(): (permission: string) => boolean {
  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  const permissions = me.data?.permissions;
  return useCallback((permission: string) => Boolean(permissions && (permissions.includes("*") || permissions.includes(permission))), [permissions]);
}
