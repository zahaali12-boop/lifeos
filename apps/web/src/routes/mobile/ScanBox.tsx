import { Button, Field, Input, useFieldControl } from "@quicker/ui";
import { Camera, CameraOff } from "lucide-react";
import { useEffect, useRef, useState, type ComponentProps } from "react";
import { useTranslation } from "react-i18next";
import { cameraAvailable, startScanning, type ScannerControls } from "./scanner";

function ScanInput(props: ComponentProps<typeof Input>) {
  const control = useFieldControl();
  return <Input {...control} {...props} />;
}

interface ScanBoxProps {
  label: string;
  hint?: string;
  error?: string | null;
  disabled?: boolean;
  /** Receives every code, whether typed, wedged in by a hardware scanner or decoded from the camera. */
  onCode: (code: string) => Promise<void> | void;
}

/** The one input of the scanner (roadmap 3.8): type or scan a code and press Enter; the camera is optional. */
export function ScanBox({ label, hint, error, disabled, onCode }: ScanBoxProps) {
  const { t } = useTranslation();
  const [value, setValue] = useState("");
  const [camera, setCamera] = useState<ScannerControls | null>(null);
  const [cameraError, setCameraError] = useState<string | null>(null);
  const video = useRef<HTMLVideoElement>(null);
  const handler = useRef(onCode);

  useEffect(() => {
    handler.current = onCode;
  });
  useEffect(() => () => { camera?.stop(); }, [camera]);

  const submit = async (code: string): Promise<void> => {
    const trimmed = code.trim();
    if (trimmed.length === 0) {
      return;
    }
    setValue("");
    await handler.current(trimmed);
  };

  const toggleCamera = async (): Promise<void> => {
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
    } catch {
      setCameraError(t("mobile.scan.cameraUnavailable"));
    }
  };

  const message = error ?? cameraError;
  return (
    <form
      className="flex flex-col gap-2"
      onSubmit={(event) => {
        event.preventDefault();
        void submit(value);
      }}
    >
      <Field label={label} {...(hint ? { description: hint } : {})} {...(message ? { error: message } : {})}>
        <div className="flex gap-2">
          <ScanInput
            value={value}
            onChange={(event) => { setValue(event.target.value); }}
            placeholder={t("mobile.scan.placeholder")}
            autoComplete="off"
            autoCapitalize="characters"
            enterKeyHint="done"
            className="h-12 min-w-0 flex-1 text-base"
            data-testid="scan-input"
            disabled={disabled}
            autoFocus
          />
          <Button type="submit" size="lg" data-testid="scan-submit" disabled={disabled}>
            {t("mobile.scan.submit")}
          </Button>
        </div>
      </Field>
      {cameraAvailable() ? (
        <Button type="button" variant="secondary" size="lg" className="gap-2" data-testid="scan-camera" onClick={() => { void toggleCamera(); }}>
          {camera ? <CameraOff aria-hidden="true" /> : <Camera aria-hidden="true" />}
          {camera ? t("mobile.scan.stopCamera") : t("mobile.scan.camera")}
        </Button>
      ) : null}
      {camera ? <p className="text-xs text-fg-muted">{t("mobile.scan.cameraOn", { engine: t(camera.engine === "native" ? "mobile.scan.engineNative" : "mobile.scan.engineZxing") })}</p> : null}
      <video ref={video} className={camera ? "w-full rounded-md bg-black" : "hidden"} muted playsInline aria-hidden="true" />
    </form>
  );
}
