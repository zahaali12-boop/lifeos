import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { api, unwrap } from "../api";

/**
 * Whether grants cover a permission, as the server decides it: "*" covers everything and a grant ending in ".*" covers
 * every key under it ("partners.customer.*" covers "partners.customer.manage"), so a role granted a whole area sees
 * every screen of it.
 */
export function covers(grants: Iterable<string>, permission: string): boolean {
  for (const grant of grants) {
    if (grant === "*" || grant === permission || (grant.endsWith(".*") && permission.startsWith(grant.slice(0, -1)))) {
      return true;
    }
  }
  return false;
}

/**
 * Whether the signed-in user holds a permission, from /me. For showing or hiding actions only: the API decides, so a
 * button shown in error is refused there.
 */
export function useCan(): (permission: string) => boolean {
  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  const permissions = me.data?.permissions;
  return useCallback((permission: string) => Boolean(permissions && covers(permissions, permission)), [permissions]);
}
