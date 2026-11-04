import { BarChart, LineChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import * as echarts from "echarts/core";
import { CanvasRenderer } from "echarts/renderers";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { formatNumber } from "../../lib/format";
import type { ChartData, ChartOptions } from "./shared";

echarts.use([BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer]);

/**
 * The categorical palette of the charts: eight hues in a fixed order, each stepped for the light and for the dark
 * surface and validated for colour-vision deficiency on adjacent pairs. A series keeps its slot whatever is filtered
 * out, and there is never a ninth hue: the data is folded into "Other" before it gets here.
 */
const lightPalette = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"];
const darkPalette = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

interface Tokens {
  isDark: boolean;
  surface: string;
  fg: string;
  fgMuted: string;
  border: string;
  font: string;
}

/** The surface and text colours the chart sits on, read from the design tokens so the chart follows the theme. */
function tokens(): Tokens {
  const style = getComputedStyle(document.documentElement);
  const isDark = document.documentElement.getAttribute("data-theme") === "dark";
  const read = (name: string, fallback: string): string => style.getPropertyValue(name).trim() || fallback;
  return {
    isDark,
    surface: read("--q-surface", isDark ? "#111a2b" : "#ffffff"),
    fg: read("--q-fg", isDark ? "#e6ebf3" : "#0f172a"),
    fgMuted: read("--q-fg-muted", isDark ? "#a7b3c5" : "#475569"),
    border: read("--q-border", isDark ? "#24304a" : "#e2e6ec"),
    font: read("--font-sans", "system-ui, sans-serif"),
  };
}

function compact(value: number): string {
  return formatNumber(value, Math.abs(value) >= 10_000 ? { notation: "compact", maximumFractionDigits: 1 } : { maximumFractionDigits: 0 });
}

/**
 * Thin marks from one baseline, hairline solid gridlines, text in the text tokens, a legend for two or more series,
 * a tooltip that lists every series at the pointer; right-to-left mirrors the axes.
 */
function buildOption(data: ChartData, options: ChartOptions, format: (value: number) => string, theme: Tokens, rtl: boolean): echarts.EChartsCoreOption {
  const isLine = options.type === "line" || options.type === "area";
  const legend = data.series.length > 1;
  const categoryAxis = {
    type: "category",
    data: data.categories,
    inverse: rtl && !options.horizontal,
    position: rtl && options.horizontal ? "right" : undefined,
    axisLine: { lineStyle: { color: theme.border } },
    axisTick: { show: false },
    axisLabel: { color: theme.fgMuted, hideOverlap: true, fontFamily: theme.font },
  };
  const valueAxis = {
    type: "value",
    inverse: rtl && options.horizontal,
    position: rtl && !options.horizontal ? "right" : undefined,
    axisLine: { show: false },
    axisTick: { show: false },
    splitLine: { lineStyle: { color: theme.border, width: 1, type: "solid" } },
    axisLabel: { color: theme.fgMuted, formatter: (value: number) => compact(value), fontFamily: theme.font },
  };
  const rounded = options.horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0];
  return {
    animationDuration: 300,
    color: theme.isDark ? darkPalette : lightPalette,
    textStyle: { fontFamily: theme.font, color: theme.fgMuted },
    // The last tick label of a value axis along the bottom overhangs the axis; the side padding keeps it whole in either direction.
    grid: { left: options.horizontal ? 32 : 12, right: options.horizontal ? 32 : 24, top: legend ? 40 : 16, bottom: 8, containLabel: true },
    legend: legend
      ? { top: 0, left: rtl ? "right" : "left", icon: isLine ? "circle" : "rect", itemWidth: 12, itemHeight: 12, textStyle: { color: theme.fgMuted, fontFamily: theme.font } }
      : undefined,
    tooltip: {
      trigger: "axis",
      axisPointer: isLine ? { type: "line", lineStyle: { color: theme.border, width: 1 } } : { type: "shadow", shadowStyle: { color: theme.border, opacity: 0.35 } },
      backgroundColor: theme.surface,
      borderColor: theme.border,
      borderWidth: 1,
      textStyle: { color: theme.fg, fontFamily: theme.font },
      valueFormatter: (value: unknown) => (typeof value === "number" ? format(value) : "—"),
    },
    xAxis: options.horizontal ? valueAxis : categoryAxis,
    yAxis: options.horizontal ? categoryAxis : valueAxis,
    series: data.series.map((s) => ({
      name: s.name,
      type: isLine ? "line" : "bar",
      stack: options.stacked ? "total" : undefined,
      data: s.values,
      barMaxWidth: 24,
      emphasis: { focus: "series" },
      // Bars: rounded at the data end, square at the baseline; a 2px surface gap separates stacked segments.
      itemStyle: isLine ? { borderColor: theme.surface, borderWidth: 2 } : { borderRadius: options.stacked ? 0 : rounded, borderColor: theme.surface, borderWidth: options.stacked ? 1 : 0 },
      lineStyle: { width: 2, cap: "round", join: "round" },
      symbol: "circle",
      symbolSize: 8,
      areaStyle: options.type === "area" ? { opacity: 0.1 } : undefined,
    })),
  };
}

/** A bar or line chart of a query result, drawn with the chart system's palette and specs; the table beside it is the accessible form. */
export function ReportChart({ data, options, format, describedBy, label }: { data: ChartData; options: ChartOptions; format: (value: number) => string; describedBy: string; label: string }) {
  const box = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const { i18n } = useTranslation();

  useEffect(() => {
    const el = box.current;
    if (!el) {
      return undefined;
    }
    const instance = echarts.init(el, undefined, { renderer: "canvas" });
    chart.current = instance;
    const observer = new ResizeObserver(() => { instance.resize(); });
    observer.observe(el);
    return () => {
      observer.disconnect();
      instance.dispose();
      chart.current = null;
    };
  }, []);

  useEffect(() => {
    const instance = chart.current;
    if (!instance) {
      return undefined;
    }
    const render = (): void => { instance.setOption(buildOption(data, options, format, tokens(), document.documentElement.dir === "rtl"), true); };
    render();
    // The theme toggle and the language switch change the tokens and the direction under the chart.
    const observer = new MutationObserver(render);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "dir"] });
    return () => { observer.disconnect(); };
  }, [data, options, format, i18n.language]);

  return <div ref={box} role="img" aria-label={label} aria-describedby={describedBy} className="h-80 w-full" data-testid="report-chart" />;
}
