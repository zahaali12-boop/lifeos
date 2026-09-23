import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, Field, Input, useFieldControl } from "@quicker/ui";
import { Camera, CameraOff } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { cameraAvailable, startScanning } from "./scanner";
function ScanInput(props) {
    const control = useFieldControl();
    return _jsx(Input, { ...control, ...props });
}
/** The one input of the scanner (roadmap 3.8): type or scan a code and press Enter; the camera is optional. */
export function ScanBox({ label, hint, error, disabled, onCode }) {
    const { t } = useTranslation();
    const [value, setValue] = useState("");
    const [camera, setCamera] = useState(null);
    const [cameraError, setCameraError] = useState(null);
    const video = useRef(null);
    const handler = useRef(onCode);
    useEffect(() => {
        handler.current = onCode;
    });
    useEffect(() => () => { camera?.stop(); }, [camera]);
    const submit = async (code) => {
        const trimmed = code.trim();
        if (trimmed.length === 0) {
            return;
        }
        setValue("");
        await handler.current(trimmed);
    };
    const toggleCamera = async () => {
        if (camera) {
            camera.stop();
            setCamera(null);
            return;
        }
        if (!video.current) {
            return;
        }
        try {
            const controls = await startScanning(video.current, (code) => { void submit(code); });
            setCameraError(null);
            setCamera(controls);
        }
        catch {
            setCameraError(t("mobile.scan.cameraUnavailable"));
        }
    };
    const message = error ?? cameraError;
    return (_jsxs("form", { className: "flex flex-col gap-2", onSubmit: (event) => {
            event.preventDefault();
            void submit(value);
        }, children: [_jsx(Field, { label: label, ...(hint ? { description: hint } : {}), ...(message ? { error: message } : {}), children: _jsxs("div", { className: "flex gap-2", children: [_jsx(ScanInput, { value: value, onChange: (event) => { setValue(event.target.value); }, placeholder: t("mobile.scan.placeholder"), autoComplete: "off", autoCapitalize: "characters", enterKeyHint: "done", className: "h-12 min-w-0 flex-1 text-base", "data-testid": "scan-input", disabled: disabled, autoFocus: true }), _jsx(Button, { type: "submit", size: "lg", "data-testid": "scan-submit", disabled: disabled, children: t("mobile.scan.submit") })] }) }), cameraAvailable() ? (_jsxs(Button, { type: "button", variant: "secondary", size: "lg", className: "gap-2", "data-testid": "scan-camera", onClick: () => { void toggleCamera(); }, children: [camera ? _jsx(CameraOff, { "aria-hidden": "true" }) : _jsx(Camera, { "aria-hidden": "true" }), camera ? t("mobile.scan.stopCamera") : t("mobile.scan.camera")] })) : null, camera ? _jsx("p", { className: "text-xs text-fg-muted", children: t("mobile.scan.cameraOn", { engine: t(camera.engine === "native" ? "mobile.scan.engineNative" : "mobile.scan.engineZxing") }) }) : null, _jsx("video", { ref: video, className: camera ? "w-full rounded-md bg-black" : "hidden", muted: true, playsInline: true, "aria-hidden": "true" })] }));
}
