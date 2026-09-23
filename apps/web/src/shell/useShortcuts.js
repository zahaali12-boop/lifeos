import { useEffect, useSyncExternalStore } from "react";
const registry = new Map();
const listeners = new Set();
let pending = null;
function emit() {
    listeners.forEach((listener) => {
        listener();
    });
}
export function registerShortcut(shortcut) {
    registry.set(shortcut.keys, shortcut);
    emit();
    return () => {
        if (registry.get(shortcut.keys) === shortcut) {
            registry.delete(shortcut.keys);
            emit();
        }
    };
}
let snapshot = [];
function currentShortcuts() {
    const next = [...registry.values()];
    if (next.length !== snapshot.length || next.some((s, i) => s !== snapshot[i])) {
        snapshot = next;
    }
    return snapshot;
}
export function useShortcutList() {
    return useSyncExternalStore((listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
    }, currentShortcuts, currentShortcuts);
}
export function useShortcut(keys, description, group, handler) {
    useEffect(() => registerShortcut({ keys, description, group, handler }), [keys, description, group, handler]);
}
export const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);
/** Human form of a shortcut for the overlay: "mod+k" → "⌘ K" or "Ctrl K"; "g c" → "G then C". */
export function describeKeys(keys, then) {
    if (keys.includes(" ")) {
        return keys
            .split(" ")
            .map((k) => k.toUpperCase())
            .join(` ${then} `);
    }
    return keys
        .split("+")
        .map((k) => (k === "mod" ? (isMac ? "⌘" : "Ctrl") : k.length === 1 ? k.toUpperCase() : k.charAt(0).toUpperCase() + k.slice(1)))
        .join(" ");
}
function isTyping(target) {
    const element = target;
    if (!element) {
        return false;
    }
    const tag = element.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || element.isContentEditable;
}
function normalise(event) {
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key.toLowerCase();
    const mod = isMac ? event.metaKey : event.ctrlKey;
    return mod ? `mod+${key}` : key;
}
export function installShortcutListener() {
    const onKeyDown = (event) => {
        if (event.defaultPrevented) {
            return;
        }
        const combo = normalise(event);
        const typing = isTyping(event.target);
        if (combo.startsWith("mod+")) {
            const shortcut = registry.get(combo);
            if (shortcut) {
                event.preventDefault();
                shortcut.handler(event);
            }
            return;
        }
        if (typing) {
            return;
        }
        if (pending && Date.now() - pending.at < 1000) {
            const sequence = registry.get(`${pending.key} ${combo}`);
            pending = null;
            if (sequence) {
                event.preventDefault();
                sequence.handler(event);
                return;
            }
        }
        const single = registry.get(combo);
        if (single) {
            event.preventDefault();
            single.handler(event);
            return;
        }
        if ([...registry.keys()].some((keys) => keys.startsWith(`${combo} `))) {
            pending = { key: combo, at: Date.now() };
        }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
        window.removeEventListener("keydown", onKeyDown);
    };
}
