const lastPingEl = document.getElementById("last-ping");
const cpuValueEl = document.getElementById("cpu-value");
const gpuValueEl = document.getElementById("gpu-value");
const ramPercentEl = document.getElementById("ram-percent");
const ramDetailsEl = document.getElementById("ram-details");
const netValueEl = document.getElementById("net-value");
const netSendEl = document.getElementById("net-send");
const netReceiveEl = document.getElementById("net-receive");
const cpuTempEl = document.getElementById("cpu-temp");
const gpuTempEl = document.getElementById("gpu-temp");
const cpuTempSourceEl = document.getElementById("cpu-temp-source");
const netSendChart = document.getElementById("net-send-chart");
const netRecvChart = document.getElementById("net-recv-chart");
const connectedStatusEl = document.getElementById("connected-status");
const lastUpdateEl = document.getElementById("last-update");
const protocol = location.protocol === "https:" ? "wss" : "ws";
const wsUrl = `${protocol}://${location.host}/ws`;
const seriesLength = 60;
let netSendSeries = new Array(seriesLength).fill(0);
let netRecvSeries = new Array(seriesLength).fill(0);

function formatPercent(value) {
  if (value === null || value === undefined) {
    return "N/A";
  }
  return `${Math.round(value)} %`;
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
    return "RAM N/A";
  }
  return `RAM ${Math.round(value)}%`;
}

function formatRamDetails(usedMb, totalMb) {
  if (!Number.isFinite(usedMb) || !Number.isFinite(totalMb)) {
    return "N/A";
  }
  return `Used: ${Math.round(usedMb)} MB / ${Math.round(totalMb)} MB`;
}

function setTempValue(element, value) {
  if (!element) {
    return;
  }
  element.textContent = formatTemp(value);
  const isHot = typeof value === "number" && value > 80;
  element.classList.toggle("warning", isHot);
}

function setConnectedStatus(isConnected) {
  if (!connectedStatusEl) {
    return;
  }
  connectedStatusEl.textContent = isConnected ? "yes" : "no";
}

function setLastUpdateStamp(value) {
  if (!lastUpdateEl) {
    return;
  }
  lastUpdateEl.textContent = value || "--";
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
  ctx.lineWidth = 1;
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
}

function drawNetworkCharts() {
  drawLineChart(netSendChart, netSendSeries, "#5bc7ff");
  drawLineChart(netRecvChart, netRecvSeries, "#8cf7a6");
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
        }
        if (gpuValueEl) {
          gpuValueEl.textContent = formatPercent(data.gpuUsagePercent);
        }
        if (ramPercentEl) {
          ramPercentEl.textContent = formatRamPercent(data.ramUsagePercent);
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
        setTempValue(cpuTempEl, data.cpuTempC);
        setTempValue(gpuTempEl, data.gpuTempC);
        if (cpuTempSourceEl) {
          if (data.cpuTempSource) {
            cpuTempSourceEl.textContent = `source: ${data.cpuTempSource}`;
            cpuTempSourceEl.hidden = false;
          } else {
            cpuTempSourceEl.textContent = "";
            cpuTempSourceEl.hidden = true;
          }
        }
        if (netValueEl) {
          const send = formatKbps(data.netSendKbps);
          const receive = formatKbps(data.netReceiveKbps);
          netValueEl.textContent = `${send} / ${receive} kbps`;
        }
        const seriesUpdated = updateSeriesFromSnapshot(data);
        if (!seriesUpdated) {
          appendSeriesValue(netSendSeries, data.netSendKbps);
          appendSeriesValue(netRecvSeries, data.netReceiveKbps);
        }
        drawNetworkCharts();
        const now = new Date().toISOString();
        if (lastPingEl) {
          lastPingEl.textContent = now;
        }
        setLastUpdateStamp(now);
      }
    } catch {
      if (lastPingEl) {
        lastPingEl.textContent = "invalid message";
      }
      setLastUpdateStamp("--");
    }
  });

  socket.addEventListener("close", () => {
    console.log("WS closed, retrying...");
    if (lastPingEl) {
      lastPingEl.textContent = "disconnected";
    }
    setConnectedStatus(false);
    setLastUpdateStamp("--");
    setTimeout(connect, 2000);
  });

  socket.addEventListener("error", (err) => {
    console.error("WS error:", err);
  });
}

setConnectedStatus(false);
setLastUpdateStamp("--");
connect();
