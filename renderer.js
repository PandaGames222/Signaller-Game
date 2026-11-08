const canvas = document.getElementById("game-canvas");
const context = canvas.getContext("2d");
const signallerButton = document.getElementById("signaller-mode");
const editorButton = document.getElementById("editor-mode");
const editorPanel = document.getElementById("editor-panel");
const buildList = document.getElementById("build-list");
const balanceDisplay = document.getElementById("balance");
const incomeDisplay = document.getElementById("income");
const clockDisplay = document.getElementById("game-clock");
const trainCountDisplay = document.getElementById("train-count");
const pauseButton = document.getElementById("pause-button");
const demolishButton = document.getElementById("demolish-button");
const toastElement = document.getElementById("toast");
const canvasHint = document.getElementById("canvas-hint");
const modeTitle = document.getElementById("mode-title");
const modeEyebrow = document.getElementById("mode-eyebrow");
const mapSubtitle = document.getElementById("map-subtitle");

const WORLD = { width: 1000, height: 600 };
const GRID_SIZE = 40;
const STARTING_BALANCE = 5000;
const BUILD_ITEMS = [
  { id: "straight", name: "Straight track", icon: "━", cost: 100 },
  { id: "curve", name: "Curved track", icon: "⌒", cost: 100 },
  { id: "switch", name: "Switch / points", icon: "⑂", cost: 500 },
  { id: "signal", name: "Signal", icon: "●", cost: 300 },
  { id: "station", name: "Station", icon: "▤", cost: 2500 },
  { id: "depot", name: "Spawner / depot", icon: "▰", cost: 1000 }
];
const PATH_POINTS = [
  { x: 220, y: 120 },
  { x: 700, y: 120 },
  { x: 830, y: 190 },
  { x: 830, y: 370 },
  { x: 730, y: 460 },
  { x: 270, y: 460 },
  { x: 170, y: 370 },
  { x: 170, y: 210 }
];

const state = {
  mode: "signaller",
  balance: STARTING_BALANCE,
  incomePerMinute: 0,
  paused: false,
  selectedItem: "straight",
  demolishMode: false,
  placedItems: [],
  signals: [
    { id: "S1", distance: 300, aspect: "green", x: 520, y: 120, side: -1 },
    { id: "S2", distance: 710, aspect: "green", x: 830, y: 280, side: 1 }
  ],
  switches: [
    { id: "P1", distance: 490, direction: "straight", x: 700, y: 120 }
  ],
  train: { distance: 80, speed: 47, route: "main", stopped: false, lastStop: "" },
  elapsed: 0,
  toastTimeout: null,
  lastFrame: 0
};

const pathSegments = [];
let pathLength = 0;

for (let index = 0; index < PATH_POINTS.length; index += 1) {
  const start = PATH_POINTS[index];
  const end = PATH_POINTS[(index + 1) % PATH_POINTS.length];
  const length = Math.hypot(end.x - start.x, end.y - start.y);
  pathSegments.push({ start, end, length, startDistance: pathLength });
  pathLength += length;
}

function resizeCanvas() {
  const bounds = canvas.getBoundingClientRect();
  const pixelRatio = window.devicePixelRatio || 1;
  canvas.width = Math.round(bounds.width * pixelRatio);
  canvas.height = Math.round(bounds.height * pixelRatio);
  context.setTransform(
    (canvas.width / WORLD.width),
    0,
    0,
    (canvas.height / WORLD.height),
    0,
    0
  );
  draw();
}

function pointAtDistance(distance) {
  const normalizedDistance = ((distance % pathLength) + pathLength) % pathLength;
  const segment = pathSegments.find(
    (part) => normalizedDistance <= part.startDistance + part.length
  ) || pathSegments[0];
  const progress = (normalizedDistance - segment.startDistance) / segment.length;
  return {
    x: segment.start.x + (segment.end.x - segment.start.x) * progress,
    y: segment.start.y + (segment.end.y - segment.start.y) * progress,
    angle: Math.atan2(segment.end.y - segment.start.y, segment.end.x - segment.start.x)
  };
}

function nextDistance(from, to) {
  return (to - from + pathLength) % pathLength;
}

function drawGrid() {
  context.save();
  context.strokeStyle = "rgba(147, 168, 180, 0.055)";
  context.lineWidth = 0.65;
  for (let x = 20; x < WORLD.width; x += GRID_SIZE) {
    context.beginPath();
    context.moveTo(x, 0);
    context.lineTo(x, WORLD.height);
    context.stroke();
  }
  for (let y = 20; y < WORLD.height; y += GRID_SIZE) {
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(WORLD.width, y);
    context.stroke();
  }
  context.restore();
}

function drawTrack(points, options = {}) {
  context.save();
  context.lineCap = "round";
  context.lineJoin = "round";

  context.beginPath();
  context.moveTo(points[0].x, points[0].y);
  for (let index = 1; index < points.length; index += 1) {
    context.lineTo(points[index].x, points[index].y);
  }
  if (options.closed) context.closePath();

  context.strokeStyle = options.color || "#28343e";
  context.lineWidth = options.width || 24;
  context.stroke();

  const path = new Path2D();
  path.moveTo(points[0].x, points[0].y);
  for (let index = 1; index < points.length; index += 1) {
    path.lineTo(points[index].x, points[index].y);
  }
  if (options.closed) path.closePath();

  context.setLineDash([]);
  context.strokeStyle = "#73808a";
  context.lineWidth = 2.5;
  context.stroke(path);
  context.strokeStyle = "#3e4b55";
  context.lineWidth = 1;

  const ties = Math.ceil((options.length || pathLength) / 17);
  for (let index = 0; index < ties; index += 1) {
    const position = pointAlongPolyline(points, (index / ties) * (options.length || pathLength), options.closed);
    if (!position) continue;
    const perpendicularX = -Math.sin(position.angle) * 8;
    const perpendicularY = Math.cos(position.angle) * 8;
    context.beginPath();
    context.moveTo(position.x - perpendicularX, position.y - perpendicularY);
    context.lineTo(position.x + perpendicularX, position.y + perpendicularY);
    context.stroke();
  }
  context.restore();
}

function pointAlongPolyline(points, distance, closed = false) {
  const segmentCount = closed ? points.length : points.length - 1;
  let remaining = distance;
  for (let index = 0; index < segmentCount; index += 1) {
    const start = points[index];
    const end = points[(index + 1) % points.length];
    const length = Math.hypot(end.x - start.x, end.y - start.y);
    if (remaining <= length || index === segmentCount - 1) {
      const progress = length === 0 ? 0 : Math.min(1, remaining / length);
      return {
        x: start.x + (end.x - start.x) * progress,
        y: start.y + (end.y - start.y) * progress,
        angle: Math.atan2(end.y - start.y, end.x - start.x)
      };
    }
    remaining -= length;
  }
  return null;
}

function drawStation() {
  context.save();
  context.fillStyle = "#1c2931";
  context.strokeStyle = "#50636c";
  context.lineWidth = 1;
  context.beginPath();
  context.roundRect(295, 402, 116, 35, 7);
  context.fill();
  context.stroke();
  context.fillStyle = "#d1e1dc";
  context.font = "600 10px 'DM Mono', monospace";
  context.fillText("GREYFIELD", 310, 417);
  context.fillStyle = "#81928f";
  context.font = "8px 'DM Mono', monospace";
  context.fillText("STATION", 310, 429);
  context.restore();
}

function drawLabels() {
  const labels = [
    { text: "NORTH LOOP", x: 420, y: 65 },
    { text: "EAST JUNCTION", x: 862, y: 284 },
    { text: "WEST APPROACH", x: 51, y: 287 }
  ];
  context.save();
  context.font = "9px 'DM Mono', monospace";
  context.fillStyle = "#657681";
  context.textAlign = "center";
  for (const label of labels) context.fillText(label.text, label.x, label.y);
  context.restore();
}

function drawBranch() {
  const branch = [{ x: 700, y: 120 }, { x: 755, y: 193 }, { x: 700, y: 260 }];
  drawTrack(branch, { width: 17, color: "#222e38", length: 181 });
  context.save();
  context.fillStyle = "#17232a";
  context.strokeStyle = "#3d515a";
  context.lineWidth = 1;
  context.beginPath();
  context.roundRect(666, 247, 70, 27, 5);
  context.fill();
  context.stroke();
  context.fillStyle = "#9caeb0";
  context.font = "8px 'DM Mono', monospace";
  context.textAlign = "center";
  context.fillText("SIDING", 701, 264);
  context.restore();
}

function drawSignal(signal) {
  const color = signal.aspect === "green"
    ? "#73e0b0"
    : signal.aspect === "yellow"
      ? "#f5bb61"
      : "#f26b72";
  const position = pointAtDistance(signal.distance);
  const signalX = position.x + Math.cos(position.angle + Math.PI / 2) * 20 * signal.side;
  const signalY = position.y + Math.sin(position.angle + Math.PI / 2) * 20 * signal.side;
  signal.hitbox = { x: signalX, y: signalY, radius: 21 };

  context.save();
  context.strokeStyle = "#77848c";
  context.lineWidth = 2;
  context.beginPath();
  context.moveTo(signalX, signalY + 9);
  context.lineTo(signalX, signalY + 29);
  context.stroke();
  context.fillStyle = "#202c34";
  context.strokeStyle = "#60717b";
  context.lineWidth = 1;
  context.beginPath();
  context.roundRect(signalX - 8, signalY - 11, 16, 23, 5);
  context.fill();
  context.stroke();
  context.shadowColor = color;
  context.shadowBlur = 10;
  context.fillStyle = color;
  context.beginPath();
  context.arc(signalX, signalY - 2, 4, 0, Math.PI * 2);
  context.fill();
  context.shadowBlur = 0;
  context.fillStyle = "#a9b5bd";
  context.font = "8px 'DM Mono', monospace";
  context.textAlign = "center";
  context.fillText(signal.id, signalX, signalY - 16);
  context.restore();
}

function drawSwitch(trackSwitch) {
  const position = pointAtDistance(trackSwitch.distance);
  const direction = trackSwitch.direction;
  trackSwitch.hitbox = { x: position.x, y: position.y, radius: 20 };

  context.save();
  context.lineCap = "round";
  context.strokeStyle = direction === "straight" ? "#73e0b0" : "#f5bb61";
  context.lineWidth = 2;
  context.beginPath();
  context.moveTo(position.x - 12, position.y - 7);
  context.lineTo(position.x + 12, position.y + 7);
  context.stroke();
  context.beginPath();
  context.moveTo(position.x - 12, position.y + 7);
  context.lineTo(position.x + 12, position.y - 7);
  context.stroke();
  context.fillStyle = direction === "straight" ? "#a5ebca" : "#f9d295";
  context.beginPath();
  context.arc(position.x, position.y, 3.5, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#a9b5bd";
  context.font = "8px 'DM Mono', monospace";
  context.textAlign = "center";
  context.fillText(`${trackSwitch.id} · ${direction.toUpperCase()}`, position.x, position.y - 16);
  context.restore();
}

function drawTrain() {
  const position = pointAtDistance(state.train.distance);
  context.save();
  context.translate(position.x, position.y);
  context.rotate(position.angle);
  context.shadowColor = "rgba(0, 0, 0, 0.55)";
  context.shadowBlur = 8;
  context.fillStyle = "#e8a94e";
  context.beginPath();
  context.roundRect(-20, -8, 40, 16, 5);
  context.fill();
  context.shadowBlur = 0;
  context.fillStyle = "#26333b";
  context.beginPath();
  context.roundRect(-12, -5, 20, 10, 2);
  context.fill();
  context.fillStyle = "#ffe1a3";
  context.fillRect(15, -3, 3, 6);
  context.fillStyle = "#c17e35";
  context.fillRect(-15, 8, 6, 2);
  context.fillRect(8, 8, 6, 2);
  context.restore();

  context.save();
  context.fillStyle = state.train.stopped ? "#f26b72" : "#e8a94e";
  context.beginPath();
  context.arc(position.x, position.y - 19, 3, 0, Math.PI * 2);
  context.fill();
  context.restore();
}

function drawPlacedItem(item) {
  const color = item.id === "signal"
    ? "#f26b72"
    : item.id === "station"
      ? "#88b9b0"
      : item.id === "depot"
        ? "#e8a94e"
        : "#82939c";

  context.save();
  context.translate(item.x, item.y);
  context.strokeStyle = color;
  context.fillStyle = "rgba(20, 29, 36, 0.95)";
  context.lineWidth = 2;
  if (item.id === "straight") {
    context.beginPath();
    context.moveTo(-17, 0);
    context.lineTo(17, 0);
    context.stroke();
    context.beginPath();
    context.moveTo(-17, -5);
    context.lineTo(17, -5);
    context.moveTo(-17, 5);
    context.lineTo(17, 5);
    context.stroke();
  } else if (item.id === "curve") {
    context.beginPath();
    context.arc(-6, 6, 20, -Math.PI / 2, 0);
    context.stroke();
    context.beginPath();
    context.arc(-6, 6, 25, -Math.PI / 2, 0);
    context.stroke();
  } else if (item.id === "switch") {
    context.beginPath();
    context.moveTo(-16, 7);
    context.lineTo(15, -7);
    context.moveTo(-16, -7);
    context.lineTo(15, 7);
    context.stroke();
  } else if (item.id === "signal") {
    context.fillStyle = color;
    context.beginPath();
    context.arc(0, 0, 6, 0, Math.PI * 2);
    context.fill();
    context.strokeRect(-2, 6, 4, 15);
  } else {
    context.beginPath();
    context.roundRect(-18, -10, 36, 20, 4);
    context.fill();
    context.stroke();
    context.fillStyle = color;
    context.font = "8px 'DM Mono', monospace";
    context.textAlign = "center";
    context.fillText(item.id === "station" ? "STN" : "DEPOT", 0, 3);
  }
  if (state.mode === "editor") {
    context.strokeStyle = "rgba(242, 107, 114, 0.65)";
    context.setLineDash([3, 3]);
    context.beginPath();
    context.arc(0, 0, 22, 0, Math.PI * 2);
    context.stroke();
  }
  context.restore();
}

function drawTrainStatus() {
  if (!state.train.stopped) return;
  const point = pointAtDistance(state.train.distance);
  context.save();
  context.fillStyle = "#301e22";
  context.strokeStyle = "#744047";
  context.lineWidth = 1;
  context.beginPath();
  context.roundRect(point.x - 39, point.y + 22, 78, 20, 5);
  context.fill();
  context.stroke();
  context.fillStyle = "#ffb0b3";
  context.font = "8px 'DM Mono', monospace";
  context.textAlign = "center";
  context.fillText(state.train.lastStop, point.x, point.y + 35);
  context.restore();
}

function draw() {
  if (!canvas.width || !canvas.height) return;
  context.clearRect(0, 0, WORLD.width, WORLD.height);
  if (state.mode === "editor") drawGrid();
  drawTrack(PATH_POINTS, { closed: true });
  drawBranch();
  drawStation();
  drawLabels();
  for (const item of state.placedItems) drawPlacedItem(item);
  for (const trackSwitch of state.switches) drawSwitch(trackSwitch);
  for (const signal of state.signals) drawSignal(signal);
  drawTrain();
  drawTrainStatus();

  if (state.mode === "editor") drawPlacementPreview();
}

function drawPlacementPreview() {
  if (!state.preview) return;
  const { x, y } = state.preview;
  context.save();
  context.strokeStyle = "rgba(115, 224, 176, 0.24)";
  context.lineWidth = 1;
  context.beginPath();
  context.arc(x, y, 22, 0, Math.PI * 2);
  context.stroke();
  context.restore();
}

function updateHUD() {
  balanceDisplay.textContent = `$${Math.floor(state.balance).toLocaleString("en-US")}`;
  incomeDisplay.innerHTML = `$${Math.floor(state.incomePerMinute).toLocaleString("en-US")} <em>/ min</em>`;
  trainCountDisplay.innerHTML = `01 <em>${state.train.stopped ? "held at signal" : "in service"}</em>`;
  const minutes = Math.floor(state.elapsed / 60);
  const seconds = Math.floor(state.elapsed % 60);
  clockDisplay.textContent = `${String(8 + minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function showToast(message) {
  toastElement.textContent = message;
  toastElement.classList.add("visible");
  if (state.toastTimeout) window.clearTimeout(state.toastTimeout);
  state.toastTimeout = window.setTimeout(() => {
    toastElement.classList.remove("visible");
    state.toastTimeout = null;
  }, 2100);
}

function renderBuildList() {
  buildList.replaceChildren();
  for (const item of BUILD_ITEMS) {
    const button = document.createElement("button");
    button.className = `build-item${state.selectedItem === item.id && !state.demolishMode ? " selected" : ""}`;
    button.type = "button";
    button.dataset.item = item.id;
    button.innerHTML = `<span class="build-icon" aria-hidden="true">${item.icon}</span>
      <span class="build-copy"><strong>${item.name}</strong><small>CLICK TO PLACE</small></span>
      <span class="build-price">$${item.cost.toLocaleString("en-US")}</span>`;
    button.addEventListener("click", () => {
      state.selectedItem = item.id;
      state.demolishMode = false;
      demolishButton.classList.remove("active");
      renderBuildList();
      showToast(`${item.name} selected — click an empty grid tile.`);
    });
    buildList.append(button);
  }
}

function setMode(mode) {
  state.mode = mode;
  const isEditor = mode === "editor";
  signallerButton.classList.toggle("active", !isEditor);
  editorButton.classList.toggle("active", isEditor);
  signallerButton.setAttribute("aria-pressed", String(!isEditor));
  editorButton.setAttribute("aria-pressed", String(isEditor));
  editorPanel.hidden = !isEditor;
  modeTitle.textContent = isEditor ? "Network editor" : "Signaller desk";
  modeEyebrow.textContent = isEditor ? "NETWORK EXPANSION" : "NETWORK OPERATIONS";
  mapSubtitle.textContent = isEditor ? "GRID BUILD MODE" : "SIGNALLING DIAGRAM";
  canvasHint.textContent = isEditor
    ? "SELECT AN ITEM TO BUILD · PLACE ON THE GRID · DEMOLISH FOR A 50% REFUND"
    : "CLICK A SIGNAL TO CHANGE ITS ASPECT · SET POINTS FOR THE MAIN LINE";
  state.demolishMode = false;
  demolishButton.classList.remove("active");
  if (isEditor) renderBuildList();
  draw();
}

function getTrainObstacle() {
  const obstacles = [];
  for (const signal of state.signals) {
    const distance = nextDistance(state.train.distance, signal.distance);
    if (distance > 0.5 && signal.aspect === "red") {
      obstacles.push({ distance, type: "signal", id: signal.id });
    }
  }
  for (const trackSwitch of state.switches) {
    const distance = nextDistance(state.train.distance, trackSwitch.distance);
    if (distance > 0.5 && trackSwitch.direction !== state.train.route) {
      obstacles.push({ distance, type: "switch", id: trackSwitch.id });
    }
  }
  obstacles.sort((first, second) => first.distance - second.distance);
  return obstacles[0] || null;
}

function tick(deltaSeconds) {
  if (state.paused || state.mode === "editor") return;
  state.elapsed += deltaSeconds * 3;
  let speedFactor = 1;
  const nextSignal = state.signals
    .map((signal) => ({ signal, distance: nextDistance(state.train.distance, signal.distance) }))
    .filter((entry) => entry.distance > 0.5)
    .sort((first, second) => first.distance - second.distance)[0];
  if (nextSignal && nextSignal.signal.aspect === "yellow") speedFactor = 0.55;

  const obstacle = getTrainObstacle();
  state.train.stopped = false;
  if (obstacle && obstacle.distance <= 17) {
    state.train.stopped = true;
    state.train.lastStop = obstacle.type === "signal"
      ? `HELD AT ${obstacle.id}`
      : `POINTS ${obstacle.id} MISALIGNED`;
  } else {
    const previousDistance = state.train.distance;
    const advance = state.train.speed * speedFactor * deltaSeconds;
    state.train.distance = (previousDistance + advance) % pathLength;
    for (const signal of state.signals) {
      if (signal.aspect !== "red" && crossedDistance(previousDistance, signal.distance, advance)) {
        state.balance += 25;
        showToast(`Safe passage at ${signal.id} — $25 section payment.`);
      }
    }
    if (crossedDistance(previousDistance, 1320, advance)) {
      state.balance += 100;
      showToast("Train arrived at Greyfield Station — $100 destination bonus.");
    }
  }

  state.incomePerMinute = (150 / (pathLength / state.train.speed)) * 60;
  updateHUD();
}

function crossedDistance(previousDistance, markerDistance, advance) {
  const distanceToMarker = nextDistance(previousDistance, markerDistance);
  return distanceToMarker > 0 && distanceToMarker <= advance;
}

function canvasPoint(event) {
  const bounds = canvas.getBoundingClientRect();
  return {
    x: ((event.clientX - bounds.left) / bounds.width) * WORLD.width,
    y: ((event.clientY - bounds.top) / bounds.height) * WORLD.height
  };
}

function snapToGrid(point) {
  return {
    x: Math.max(20, Math.min(WORLD.width - 20, Math.round(point.x / GRID_SIZE) * GRID_SIZE)),
    y: Math.max(20, Math.min(WORLD.height - 20, Math.round(point.y / GRID_SIZE) * GRID_SIZE))
  };
}

function placeItem(point) {
  const item = BUILD_ITEMS.find((buildItem) => buildItem.id === state.selectedItem);
  if (!item) return;
  if (state.balance < item.cost) {
    showToast(`Not enough funds — ${item.name} costs $${item.cost.toLocaleString("en-US")}.`);
    return;
  }
  const occupied = state.placedItems.some(
    (placed) => Math.hypot(placed.x - point.x, placed.y - point.y) < GRID_SIZE * 0.75
  );
  if (occupied) {
    showToast("That tile is already occupied.");
    return;
  }
  state.balance -= item.cost;
  state.placedItems.push({ id: item.id, x: point.x, y: point.y, cost: item.cost });
  updateHUD();
  draw();
  showToast(`${item.name} placed for $${item.cost.toLocaleString("en-US")}.`);
}

function demolishAt(point) {
  let nearestIndex = -1;
  let nearestDistance = 25;
  for (let index = 0; index < state.placedItems.length; index += 1) {
    const item = state.placedItems[index];
    const distance = Math.hypot(item.x - point.x, item.y - point.y);
    if (distance < nearestDistance) {
      nearestDistance = distance;
      nearestIndex = index;
    }
  }
  if (nearestIndex < 0) {
    showToast("Select one of your placed pieces to demolish it.");
    return;
  }
  const [removed] = state.placedItems.splice(nearestIndex, 1);
  const refund = Math.floor(removed.cost * 0.5);
  state.balance += refund;
  updateHUD();
  draw();
  showToast(`Piece demolished — $${refund.toLocaleString("en-US")} refunded.`);
}

function handleMapClick(point) {
  if (state.mode === "editor") {
    const snappedPoint = snapToGrid(point);
    if (state.demolishMode) demolishAt(snappedPoint);
    else placeItem(snappedPoint);
    return;
  }

  for (const signal of state.signals) {
    if (Math.hypot(signal.hitbox.x - point.x, signal.hitbox.y - point.y) <= signal.hitbox.radius) {
      const aspects = ["red", "yellow", "green"];
      signal.aspect = aspects[(aspects.indexOf(signal.aspect) + 1) % aspects.length];
      showToast(`${signal.id} set to ${signal.aspect.toUpperCase()}.`);
      draw();
      return;
    }
  }
  for (const trackSwitch of state.switches) {
    if (Math.hypot(trackSwitch.hitbox.x - point.x, trackSwitch.hitbox.y - point.y) <= trackSwitch.hitbox.radius) {
      trackSwitch.direction = trackSwitch.direction === "straight" ? "diverging" : "straight";
      showToast(`${trackSwitch.id} set ${trackSwitch.direction.toUpperCase()}.`);
      draw();
      return;
    }
  }
}

signallerButton.addEventListener("click", () => setMode("signaller"));
editorButton.addEventListener("click", () => setMode("editor"));
pauseButton.addEventListener("click", () => {
  state.paused = !state.paused;
  pauseButton.classList.toggle("paused", state.paused);
  pauseButton.textContent = state.paused ? "▶" : "Ⅱ";
  pauseButton.setAttribute("aria-label", state.paused ? "Resume simulation" : "Pause simulation");
  showToast(state.paused ? "Simulation paused." : "Simulation resumed.");
});
demolishButton.addEventListener("click", () => {
  state.demolishMode = !state.demolishMode;
  demolishButton.classList.toggle("active", state.demolishMode);
  if (state.demolishMode) {
    for (const button of buildList.querySelectorAll(".build-item")) button.classList.remove("selected");
    showToast("Demolish mode — click a placed piece to remove it.");
  } else {
    renderBuildList();
  }
});
canvas.addEventListener("click", (event) => handleMapClick(canvasPoint(event)));
canvas.addEventListener("pointermove", (event) => {
  if (state.mode !== "editor") return;
  state.preview = snapToGrid(canvasPoint(event));
  draw();
});
canvas.addEventListener("pointerleave", () => {
  state.preview = null;
  if (state.mode === "editor") draw();
});
window.addEventListener("resize", resizeCanvas);

function animate(timestamp) {
  const deltaSeconds = state.lastFrame
    ? Math.min((timestamp - state.lastFrame) / 1000, 0.05)
    : 0;
  state.lastFrame = timestamp;
  tick(deltaSeconds);
  draw();
  window.requestAnimationFrame(animate);
}

updateHUD();
resizeCanvas();
window.requestAnimationFrame(animate);
