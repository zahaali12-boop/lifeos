const formats = ["ean_13", "ean_8", "upc_a", "upc_e", "code_128", "code_39", "itf", "qr_code", "data_matrix"];
function nativeDetector() {
    const candidate = globalThis.BarcodeDetector;
    return candidate ?? null;
}
/** True when the device can offer a camera at all; the actual permission is asked when scanning starts. */
export function cameraAvailable() {
    return typeof navigator !== "undefined" && "mediaDevices" in navigator && typeof navigator.mediaDevices.getUserMedia === "function";
}
/** Starts decoding into `video`; `onCode` fires once per distinct code with a short cool-down so one label is not read ten times. */
export async function startScanning(video, onCode) {
    let lastCode = "";
    let lastAt = 0;
    const emit = (code) => {
        const now = Date.now();
        if (code === lastCode && now - lastAt < 1500) {
            return;
        }
        lastCode = code;
        lastAt = now;
        onCode(code);
    };
    const Native = nativeDetector();
    if (Native) {
        const supported = await Native.getSupportedFormats();
        const detector = new Native({ formats: formats.filter((f) => supported.includes(f)) });
        const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" } });
        video.srcObject = stream;
        await video.play();
        const state = { stopped: false };
        const isStopped = () => state.stopped;
        const tick = async () => {
            if (isStopped()) {
                return;
            }
            try {
                const found = await detector.detect(video);
                const first = found[0];
                if (first && first.rawValue.length > 0) {
                    emit(first.rawValue);
                }
            }
            catch {
                // a frame that could not be decoded: try the next one
            }
            if (!isStopped()) {
                setTimeout(() => { void tick(); }, 200);
            }
        };
        void tick();
        return {
            engine: "native",
            stop: () => {
                state.stopped = true;
                stream.getTracks().forEach((track) => { track.stop(); });
                video.srcObject = null;
            },
        };
    }
    const { BrowserMultiFormatReader } = await import("@zxing/browser");
    const reader = new BrowserMultiFormatReader();
    const controls = await reader.decodeFromVideoDevice(undefined, video, (result) => {
        if (result) {
            emit(result.getText());
        }
    });
    return { engine: "zxing", stop: () => { controls.stop(); } };
}
