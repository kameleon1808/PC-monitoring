const lastPingEl = document.getElementById("last-ping");
const cpuValueEl = document.getElementById("cpu-value");
const cpuGaugeEl = document.getElementById("cpu-gauge");
const gpuValueEl = document.getElementById("gpu-value");
const gpuGaugeEl = document.getElementById("gpu-gauge");
const ramPercentEl = document.getElementById("ram-percent");
const ramDetailsEl = document.getElementById("ram-details");
const ramBarFillEl = document.getElementById("ram-bar-fill");
const netSendEl = document.getElementById("net-send");
const netReceiveEl = document.getElementById("net-receive");
const cpuTempEl = document.getElementById("cpu-temp");
const gpuTempEl = document.getElementById("gpu-temp");
const cpuTempFillEl = document.getElementById("cpu-temp-fill");
const gpuTempFillEl = document.getElementById("gpu-temp-fill");
const cpuTempSourceEl = document.getElementById("cpu-temp-source");
const cpuTempBadgeEl = document.getElementById("cpu-temp-badge");
const cpuTempHintEl = document.getElementById("cpu-temp-hint");
const netSendChart = document.getElementById("net-send-chart");
const netRecvChart = document.getElementById("net-recv-chart");
const netTotalChart = document.getElementById("net-total-chart");
const connectedStatusEl = document.getElementById("connected-status");
const lastUpdateEl = document.getElementById("last-update");
const processTableBody = document.getElementById("process-table-body");
const protocol = location.protocol === "https:" ? "wss" : "ws";
const wsUrl = `${protocol}://${location.host}/ws`;
const seriesLength = 60;
let netSendSeries = new Array(seriesLength).fill(0);
let netRecvSeries = new Array(seriesLength).fill(0);

function formatPercent(value) {
  if (value === null || value === undefined) {
    return "N/A";
  }
  return `${Math.round(value)}%`;
}

function formatKbps(value) {
  if (value === null || value === undefined) {
    return "--";
  }
  return `${value}`;
}

function formatTemp(value) {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return "N/A";
  }
  const rounded = Math.round(value * 10) / 10;
  return `${rounded.toFixed(1)} °C`;
}

function formatRamPercent(value) {
  if (value === null || value === undefined) {
    return "--%";
  }
  return `${Math.round(value)}%`;
}

function formatRamDetails(usedMb, totalMb) {
  if (!Number.isFinite(usedMb) || !Number.isFinite(totalMb)) {
    return "N/A";
  }
  const usedGb = usedMb / 1024;
  const totalGb = totalMb / 1024;
  return `${usedGb.toFixed(1)} GB / ${totalGb.toFixed(1)} GB`;
}

function pad2(value) {
  return value.toString().padStart(2, "0");
}

function formatDateStamp(date) {
  if (!(date instanceof Date) || Number.isNaN(date.getTime())) {
    return "--";
  }
  const day = pad2(date.getDate());
  const month = pad2(date.getMonth() + 1);
  const year = date.getFullYear();
  return `${day}.${month}.${year}`;
}

function formatTimeStamp(date) {
  if (!(date instanceof Date) || Number.isNaN(date.getTime())) {
    return "--";
  }
  const hours = pad2(date.getHours());
  const minutes = pad2(date.getMinutes());
  return `${hours}:${minutes}`;
}

function clampPercent(value) {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Math.min(100, Math.max(0, value));
}

function setGaugeValue(element, value) {
  if (!element) {
    return;
  }
  element.style.setProperty("--value", clampPercent(value));
}

function setBarValue(element, value) {
  if (!element) {
    return;
  }
  element.style.width = `${clampPercent(value)}%`;
}

function setTempValue(element, value, fillEl) {
  if (!element) {
    return;
  }
  element.textContent = formatTemp(value);
  const isHot = typeof value === "number" && value > 80;
  element.classList.toggle("warning", isHot);
  if (fillEl) {
    const percent = clampPercent(value);
    fillEl.style.height = `${percent}%`;
  }
}

function getCpuTempBadge(status) {
  switch (status) {
    case "warming_up":
      return "Warming";
    case "blocked_or_policy":
      return "Blocked";
    case "wmi_approx":
      return "Approx";
    case "external_not_configured":
      return "N/A";
    case "no_sensors":
    case "no_values":
      return "N/A";
    default:
      return "N/A";
  }
}

function getCpuTempHint(status) {
  switch (status) {
    case "warming_up":
      return "CPU temp warming up.";
    case "blocked_or_policy":
      return "CPU temp blocked by policy.";
    case "wmi_approx":
      return "WMI ThermalZone is approximate.";
    case "external_not_configured":
      return "External provider not configured.";
    case "no_sensors":
      return "CPU temp sensors not found.";
    case "no_values":
      return "CPU temp sensors report no values.";
    default:
      return "CPU temp unavailable.";
  }
}

function updateCpuTempStatus(data) {
  if (!cpuTempBadgeEl || !cpuTempHintEl) {
    return;
  }
  const status = data?.cpuTempStatus;
  if (!status || status === "ok") {
    cpuTempBadgeEl.textContent = "";
    cpuTempBadgeEl.hidden = true;
    cpuTempHintEl.textContent = "";
    cpuTempHintEl.hidden = true;
    return;
  }
  cpuTempBadgeEl.textContent = getCpuTempBadge(status);
  cpuTempBadgeEl.hidden = false;
  const hint = data?.cpuTempHint || getCpuTempHint(status);
  cpuTempHintEl.textContent = hint;
  cpuTempHintEl.hidden = !hint;
}

function setConnectedStatus(isConnected) {
  if (!connectedStatusEl) {
    return;
  }
  connectedStatusEl.textContent = isConnected ? "online" : "offline";
  connectedStatusEl.classList.toggle("is-online", isConnected);
}

function setLastPingStamp(date) {
  if (!lastPingEl) {
    return;
  }
  lastPingEl.textContent = formatDateStamp(date);
}

function setLastUpdateStamp(value) {
  if (!lastUpdateEl) {
    return;
  }
  lastUpdateEl.textContent = formatTimeStamp(value);
}

function ensureSeriesLength(values, fallback) {
  if (!Array.isArray(values) || values.length !== seriesLength) {
    return fallback;
  }
  return values.slice();
}

function appendSeriesValue(series, value) {
  const nextValue = value === null || value === undefined ? 0 : value;
  series.push(nextValue);
  if (series.length > seriesLength) {
    series.shift();
  }
}

function updateSeriesFromSnapshot(data) {
  if (!data || !data.series) {
    return false;
  }
  netSendSeries = ensureSeriesLength(data.series.netSend60, netSendSeries);
  netRecvSeries = ensureSeriesLength(data.series.netRecv60, netRecvSeries);
  return true;
}

function drawLineChart(canvas, series, strokeStyle) {
  if (!canvas) {
    return;
  }
  const ctx = canvas.getContext("2d");
  if (!ctx) {
    return;
  }
  const width = Math.max(1, canvas.clientWidth);
  const height = Math.max(1, canvas.clientHeight);
  const scale = window.devicePixelRatio || 1;
  const scaledWidth = Math.floor(width * scale);
  const scaledHeight = Math.floor(height * scale);
  if (canvas.width !== scaledWidth || canvas.height !== scaledHeight) {
    canvas.width = scaledWidth;
    canvas.height = scaledHeight;
  }
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  ctx.clearRect(0, 0, width, height);

  let maxValue = 1;
  for (let i = 0; i < series.length; i += 1) {
    const value = series[i] ?? 0;
    if (value > maxValue) {
      maxValue = value;
    }
  }

  const padding = 2;
  const plotHeight = Math.max(1, height - padding * 2);
  const stepX = series.length > 1 ? width / (series.length - 1) : width;

  ctx.strokeStyle = strokeStyle;
  ctx.lineWidth = 1.6;
  ctx.shadowColor = strokeStyle;
  ctx.shadowBlur = 6;
  ctx.beginPath();
  for (let i = 0; i < series.length; i += 1) {
    const value = Math.max(0, series[i] ?? 0);
    const x = i * stepX;
    const y = height - padding - (value / maxValue) * plotHeight;
    if (i === 0) {
      ctx.moveTo(x, y);
    } else {
      ctx.lineTo(x, y);
    }
  }
  ctx.stroke();
  ctx.shadowBlur = 0;
}

function drawNetworkCharts() {
  drawLineChart(netSendChart, netSendSeries, "#66e7ff");
  drawLineChart(netRecvChart, netRecvSeries, "#b07bff");
  if (netTotalChart) {
    const totalSeries = netSendSeries.map((value, index) => (value ?? 0) + (netRecvSeries[index] ?? 0));
    drawLineChart(netTotalChart, totalSeries, "#7cf0ff");
  }
}

function formatUsageValue(value) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  return Math.round(value).toString();
}

function renderProcessRow(proc) {
  const name = proc?.name || proc?.processName || "Unknown";
  const cpu = Number(proc?.cpuPercent ?? proc?.cpu ?? proc?.cpuUsagePercent);
  const ram = Number(proc?.ramPercent ?? proc?.ram ?? proc?.ramUsagePercent);
  const gpu = Number(proc?.gpuPercent ?? proc?.gpu ?? proc?.gpuUsagePercent);
  return `
    <tr>
      <td>${name}</td>
      <td><span class="usage-chip cpu" style="--value: ${clampPercent(cpu)}">${formatUsageValue(cpu)}</span></td>
      <td><span class="usage-chip ram" style="--value: ${clampPercent(ram)}">${formatUsageValue(ram)}</span></td>
      <td><span class="usage-chip gpu" style="--value: ${clampPercent(gpu)}">${formatUsageValue(gpu)}</span></td>
    </tr>
  `;
}

function updateProcessTable(processes) {
  if (!processTableBody) {
    return;
  }
  if (!Array.isArray(processes) || processes.length === 0) {
    processTableBody.innerHTML = `
      <tr>
        <td colspan="4">Waiting for process data...</td>
      </tr>
    `;
    return;
  }
  processTableBody.innerHTML = processes.slice(0, 5).map(renderProcessRow).join("");
}

function connect() {
  const socket = new WebSocket(wsUrl);

  socket.addEventListener("open", () => {
    console.log("WS connected:", wsUrl);
    setConnectedStatus(true);
  });

  socket.addEventListener("message", (event) => {
    try {
      const payload = JSON.parse(event.data);
      if ((payload?.type === "metrics" || payload?.type === "init" || payload?.type === "series") && payload.data) {
        const data = payload.data;
        if (cpuValueEl) {
          cpuValueEl.textContent = formatPercent(data.cpuPercent);
          setGaugeValue(cpuGaugeEl, data.cpuPercent);
        }
        if (gpuValueEl) {
          gpuValueEl.textContent = formatPercent(data.gpuUsagePercent);
          setGaugeValue(gpuGaugeEl, data.gpuUsagePercent);
        }
        if (ramPercentEl) {
          ramPercentEl.textContent = formatRamPercent(data.ramUsagePercent);
          setBarValue(ramBarFillEl, data.ramUsagePercent);
        }
        if (ramDetailsEl) {
          ramDetailsEl.textContent = formatRamDetails(data.ramUsedMb, data.ramTotalMb);
        }
        if (netSendEl) {
          netSendEl.textContent = formatKbps(data.netSendKbps);
        }
        if (netReceiveEl) {
          netReceiveEl.textContent = formatKbps(data.netReceiveKbps);
        }
        setTempValue(cpuTempEl, data.cpuTempC, cpuTempFillEl);
        setTempValue(gpuTempEl, data.gpuTempC, gpuTempFillEl);
        if (cpuTempSourceEl) {
          if (data.cpuTempSource) {
            cpuTempSourceEl.textContent = `source: ${data.cpuTempSource}`;
            cpuTempSourceEl.hidden = false;
          } else {
            cpuTempSourceEl.textContent = "";
            cpuTempSourceEl.hidden = true;
          }
        }
        updateCpuTempStatus(data);
        updateProcessTable(data?.topProcesses);
        const seriesUpdated = updateSeriesFromSnapshot(data);
        if (!seriesUpdated) {
          appendSeriesValue(netSendSeries, data.netSendKbps);
          appendSeriesValue(netRecvSeries, data.netReceiveKbps);
        }
        drawNetworkCharts();
        const now = new Date();
        setLastPingStamp(now);
        setLastUpdateStamp(now);
      }
    } catch {
      setLastPingStamp(null);
    }
  });

  socket.addEventListener("close", () => {
    console.log("WS closed, retrying...");
    setConnectedStatus(false);
    setLastPingStamp(null);
    setTimeout(connect, 2000);
  });

  socket.addEventListener("error", (err) => {
    console.error("WS error:", err);
  });
}

setConnectedStatus(false);
setLastPingStamp(null);
setLastUpdateStamp(new Date());
setInterval(() => {
  setLastUpdateStamp(new Date());
}, 1000);
connect();
