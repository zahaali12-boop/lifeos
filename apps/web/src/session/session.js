import { useSyncExternalStore } from "react";
const STORAGE_KEY = "quicker.session";
const listeners = new Set();
let current = load();
function load() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : null;
    }
    catch {
        return null;
    }
}
function emit() {
    listeners.forEach((listener) => {
        listener();
    });
}
export function getSession() {
    return current;
}
export function setSession(tokens) {
    current = { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, tenant: tokens.tenant, user: tokens.user, membershipId: tokens.membershipId };
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
    }
    catch {
        // ignore
    }
    emit();
    return current;
}
export function clearSession() {
    current = null;
    try {
        localStorage.removeItem(STORAGE_KEY);
    }
    catch {
        // ignore
    }
    emit();
}
export function useSession() {
    return useSyncExternalStore((listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
    }, getSession, getSession);
}
