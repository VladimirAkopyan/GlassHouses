// --- Map (Leaflet) ---
const GMV_CENTER = [51.4970, 0.0080];
let mapInstance = null;
const placedPins = []; // { name, marker }

function initMap() {
  const el = document.querySelector("#map");
  if (!el || typeof L === "undefined") return;
  mapInstance = L.map(el, { zoomControl: true, scrollWheelZoom: false }).setView(GMV_CENTER, 15);
  L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
  }).addTo(mapInstance);
}

function dropPinFor(location) {
  if (!mapInstance || !location) return;
  // Dim previous pins so the latest is the visual focal point.
  for (const p of placedPins) {
    p.marker.setOpacity(0.5);
    if (p.marker.getElement()) p.marker.getElement().classList.remove("pinLatest");
    if (p.marker.getElement()) p.marker.getElement().classList.add("pinHistory");
  }

  const marker = L.marker([location.lat, location.lng]).addTo(mapInstance);
  marker.bindPopup(`<strong>${location.label}</strong><br><small>matched: "${location.name}"</small>`);
  placedPins.push({ name: location.name, marker });

  // Pan + open popup. flyTo gives a smooth animation.
  mapInstance.flyTo([location.lat, location.lng], 16, { duration: 0.8 });
  marker.openPopup();

  const status = document.querySelector("#mapStatus");
  if (status) {
    status.textContent = `Latest: ${location.label} · ${placedPins.length} pin${placedPins.length === 1 ? "" : "s"} this session`;
  }
}

const messages = document.querySelector("#messages");
const form = document.querySelector("#chatForm");
const question = document.querySelector("#question");
const healthDot = document.querySelector("#healthDot");
const docCount = document.querySelector("#docCount");
const retrievalMode = document.querySelector("#retrievalMode");
const llmStatus = document.querySelector("#llmStatus");
const lessonsToggle = document.querySelector("#lessonsToggle");
const lessonsToggleLabel = document.querySelector("#lessonsToggleLabel");
const complaintsToggle = document.querySelector("#complaintsToggle");
const complaintsToggleLabel = document.querySelector("#complaintsToggleLabel");
const lessonsList = document.querySelector("#lessonsList");
const lessonsCount = document.querySelector("#lessonsCount");
const learnBtn = document.querySelector("#learnBtn");
const toast = document.querySelector("#toast");

let lastAppliedLessonIds = new Set();

function showToast(text, ms = 4500) {
  toast.textContent = text;
  toast.classList.add("show");
  clearTimeout(showToast._t);
  showToast._t = setTimeout(() => toast.classList.remove("show"), ms);
}

function addMessage(role, text, extras = {}) {
  const node = document.createElement("div");
  node.className = `message ${role}`;
  const sources = extras.sources ?? [];
  const messageId = `m${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`;

  if (role === "assistant" && sources.length > 0) {
    renderAnswerWithCitations(node, text, sources, messageId);
  } else {
    const body = document.createElement("p");
    body.textContent = text;
    node.appendChild(body);
  }

  const complaints = extras.complaints ?? [];
  if (complaints.length > 0) {
    const complaintList = document.createElement("div");
    complaintList.className = "complaints";
    const complaintTitle = document.createElement("div");
    complaintTitle.className = "complaintTitle";
    complaintTitle.textContent = "Resident complaints:";
    complaintList.appendChild(complaintTitle);
    for (const complaint of complaints.slice(0, 5)) {
      const item = document.createElement("div");
      item.className = "complaintItem";
      item.innerHTML = `<strong>${complaint.buildingName}</strong> [${complaint.category}]: ${complaint.description}`;
      complaintList.appendChild(item);
    }
    node.appendChild(complaintList);
  }

  if (role === "assistant" && extras.chatId) {
    node.appendChild(buildAnswerFooter(extras));
  }

  messages.appendChild(node);
  messages.scrollTop = messages.scrollHeight;
  return node;
}

// Renders a single line/paragraph of text into `container`, expanding **bold** runs and
// citation markers (`[source_id#chunk_index]`) into nodes. Anything not matched is plain text.
function renderInline(container, text, ctx) {
  const citeRe = /\[([^\]#]+?)#(\d+)\]/g;
  const boldRe = /\*\*([^*\n]+?)\*\*/g;
  const tokens = [];
  let m;
  while ((m = citeRe.exec(text)) !== null) {
    tokens.push({ type: "cite", start: m.index, end: m.index + m[0].length, sourceId: m[1].trim(), raw: m[0] });
  }
  while ((m = boldRe.exec(text)) !== null) {
    tokens.push({ type: "bold", start: m.index, end: m.index + m[0].length, content: m[1] });
  }
  tokens.sort((a, b) => a.start - b.start);

  let cursor = 0;
  for (const t of tokens) {
    if (t.start < cursor) continue; // skip overlapping (e.g. bold inside citation, unlikely)
    if (t.start > cursor) container.appendChild(document.createTextNode(text.slice(cursor, t.start)));
    if (t.type === "cite") {
      const num = ctx.sourceIdToNum.get(t.sourceId);
      if (num) {
        const a = document.createElement("a");
        a.href = `#cite-${ctx.messageId}-${num}`;
        a.className = "citation";
        a.textContent = `[${num}]`;
        a.title = t.sourceId;
        container.appendChild(a);
      } else {
        container.appendChild(document.createTextNode(t.raw));
      }
    } else if (t.type === "bold") {
      const strong = document.createElement("strong");
      strong.textContent = t.content;
      container.appendChild(strong);
    }
    cursor = t.end;
  }
  if (cursor < text.length) container.appendChild(document.createTextNode(text.slice(cursor)));
}

// Transforms Claude's inline `[source_id#chunk_index]` markers into clean numbered footnotes,
// and renders light markdown (bullets, **bold**, paragraphs). Multiple chunks from the same
// source share a citation number. Inline markers become anchor links to the corresponding
// entry in the citations list at the bottom; each list entry hyperlinks to the actual document
// via /api/source/<filename>.
function renderAnswerWithCitations(node, text, sources, messageId) {
  const sourceById = new Map();
  for (const s of sources) sourceById.set(`${s.sourceId}#${s.chunkIndex}`, s);

  // First pass: walk the text, assign a citation number per unique source_id (in order of appearance).
  const sourceIdToNum = new Map();
  const orderedSourceIds = [];
  const chunksBySourceId = new Map();
  const citeRe = /\[([^\]#]+?)#(\d+)\]/g;
  let cm;
  while ((cm = citeRe.exec(text)) !== null) {
    const sourceId = cm[1].trim();
    const chunkIdx = parseInt(cm[2], 10);
    if (!sourceIdToNum.has(sourceId)) {
      sourceIdToNum.set(sourceId, orderedSourceIds.length + 1);
      orderedSourceIds.push(sourceId);
      chunksBySourceId.set(sourceId, new Set());
    }
    chunksBySourceId.get(sourceId).add(chunkIdx);
  }

  // Second pass: parse markdown blocks (paragraph / bullet list / numbered list) and render each.
  const ctx = { messageId, sourceIdToNum };
  const blocks = text.replace(/\r\n/g, "\n").split(/\n\s*\n/);
  const bulletRe = /^[-*]\s+/;
  const numberedRe = /^\d+\.\s+/;
  // Strip markdown heading prefixes — we tell Claude not to use them but be defensive.
  const headingRe = /^#+\s+/;

  for (const raw of blocks) {
    const lines = raw.split("\n").map(l => l.replace(headingRe, "").trimEnd()).filter(l => l.trim().length > 0);
    if (lines.length === 0) continue;

    const allBullets = lines.every(l => bulletRe.test(l));
    const allNumbered = !allBullets && lines.every(l => numberedRe.test(l));

    if (allBullets) {
      const ul = document.createElement("ul");
      ul.className = "answerList";
      for (const line of lines) {
        const li = document.createElement("li");
        renderInline(li, line.replace(bulletRe, ""), ctx);
        ul.appendChild(li);
      }
      node.appendChild(ul);
    } else if (allNumbered) {
      const ol = document.createElement("ol");
      ol.className = "answerList";
      for (const line of lines) {
        const li = document.createElement("li");
        renderInline(li, line.replace(numberedRe, ""), ctx);
        ol.appendChild(li);
      }
      node.appendChild(ol);
    } else {
      const p = document.createElement("p");
      p.className = "answerBody";
      // Soft line breaks within a paragraph become spaces (markdown convention).
      renderInline(p, lines.join(" "), ctx);
      node.appendChild(p);
    }
  }

  // Citations list at the bottom — one entry per unique source_id, hyperlinked to the file.
  if (orderedSourceIds.length > 0) {
    const list = document.createElement("ol");
    list.className = "citationsList";
    for (let i = 0; i < orderedSourceIds.length; i++) {
      const sid = orderedSourceIds[i];
      const num = i + 1;
      // Find a sample chunk for this source_id to extract filename / page / type from.
      let sample = null;
      for (const ck of chunksBySourceId.get(sid)) {
        const found = sourceById.get(`${sid}#${ck}`);
        if (found) { sample = found; break; }
      }
      const li = document.createElement("li");
      li.id = `cite-${messageId}-${num}`;
      li.className = "citationItem";
      li.value = num;

      const title = document.createElement("span");
      title.className = "citationTitle";
      if (sample?.filename) {
        const a = document.createElement("a");
        a.href = `/api/source/${encodeURIComponent(sample.filename)}`;
        a.target = "_blank";
        a.rel = "noopener";
        a.textContent = sid;
        title.appendChild(a);
      } else {
        title.textContent = sid;
      }
      li.appendChild(title);

      const meta = document.createElement("span");
      meta.className = "citationMeta";
      const parts = [];
      if (sample?.sourceType) parts.push(sample.sourceType);
      if (sample?.filename) parts.push(sample.filename);
      if (sample?.page) parts.push(`p.${sample.page}`);
      const chunks = [...chunksBySourceId.get(sid)].sort((a, b) => a - b);
      if (chunks.length > 0) parts.push(`chunk${chunks.length === 1 ? "" : "s"} ${chunks.join(", ")}`);
      meta.textContent = parts.join(" · ");
      li.appendChild(meta);

      list.appendChild(li);
    }
    node.appendChild(list);
  }
}

function buildAnswerFooter(extras) {
  const footer = document.createElement("div");
  footer.className = "answerFooter";

  const top = Number(extras.topScore || 0);
  const baseline = Number(extras.avgScoreBaseline || 0);
  const lessonsApplied = extras.appliedLessons ?? [];
  const searchQueries = extras.searchQueries ?? [];

  // Search trail: shows the queries Claude reformulated through to find evidence.
  // This is the visible "adaptive retrieval" signal — proof Claude is doing more than one search.
  if (searchQueries.length > 0) {
    const trail = document.createElement("div");
    trail.className = "searchTrail";
    const label = document.createElement("span");
    label.className = "searchTrailLabel";
    label.textContent = `🔎 Claude searched ${searchQueries.length}× ·`;
    trail.appendChild(label);
    for (const q of searchQueries) {
      const chip = document.createElement("span");
      chip.className = "searchQueryChip";
      chip.textContent = q;
      chip.title = q;
      trail.appendChild(chip);
    }
    footer.appendChild(trail);
  }

  const scoreChip = document.createElement("span");
  scoreChip.className = "scoreChip" + (lessonsApplied.length > 0 && top > baseline ? " up" : "");
  let label = `top ${top.toFixed(2)}`;
  if (lessonsApplied.length > 0 && baseline > 0) {
    const delta = top - baseline;
    const pct = ((delta / baseline) * 100).toFixed(0);
    const arrow = delta >= 0 ? "↑" : "↓";
    label += ` · vs baseline ${baseline.toFixed(2)} ${arrow}${pct}%`;
  }
  scoreChip.textContent = label;
  footer.appendChild(scoreChip);

  const modeChip = document.createElement("span");
  modeChip.className = "lessonChip";
  if (extras.usedLessonsMode) {
    modeChip.textContent = lessonsApplied.length > 0
      ? `${lessonsApplied.length} lesson${lessonsApplied.length > 1 ? "s" : ""} applied`
      : "lessons ON · none matched";
  } else {
    modeChip.style.background = "rgba(169, 79, 62, 0.08)";
    modeChip.style.borderColor = "rgba(169, 79, 62, 0.4)";
    modeChip.style.color = "var(--brick)";
    modeChip.textContent = "lessons OFF";
  }
  footer.appendChild(modeChip);

  for (const lesson of lessonsApplied) {
    const tag = document.createElement("span");
    tag.className = "lessonChip";
    tag.title = lesson.lessonText;
    tag.textContent = lesson.questionPattern;
    footer.appendChild(tag);
  }

  const feedbackBox = buildFeedbackBox(extras.chatId);
  footer.appendChild(feedbackBox);

  return footer;
}

function buildFeedbackBox(chatId) {
  const wrap = document.createElement("div");
  wrap.className = "feedbackBox";

  let chosenRating = 0;

  const stars = document.createElement("div");
  stars.className = "starRow";
  const starButtons = [];
  for (let i = 1; i <= 5; i++) {
    const star = document.createElement("button");
    star.type = "button";
    star.className = "star";
    star.textContent = "★";
    star.dataset.value = String(i);
    star.title = `${i} star${i === 1 ? "" : "s"}`;
    star.addEventListener("mouseenter", () => paintStars(starButtons, i));
    star.addEventListener("mouseleave", () => paintStars(starButtons, chosenRating));
    star.addEventListener("click", () => {
      chosenRating = i;
      paintStars(starButtons, chosenRating);
      input.disabled = false;
      submit.disabled = false;
    });
    stars.appendChild(star);
    starButtons.push(star);
  }
  wrap.appendChild(stars);

  const input = document.createElement("input");
  input.type = "text";
  input.placeholder = "Optional: what was missing, wrong, or right?";
  input.className = "feedbackInput";
  input.disabled = true;

  const submit = document.createElement("button");
  submit.type = "button";
  submit.textContent = "Teach";
  submit.title = "Submit your rating (and optional notes) so the agent learns";
  submit.className = "feedbackSubmit";
  submit.disabled = true;

  const send = async () => {
    if (chosenRating < 1) return;
    const text = input.value.trim();
    submit.disabled = true;
    const original = submit.textContent;
    submit.textContent = "…";
    try {
      const res = await fetch("/api/rate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ chatId, rating: chosenRating, feedback: text || null })
      });
      const data = await res.json();
      if (!res.ok) {
        showToast(data.error ?? "Feedback failed.");
        submit.disabled = false;
        submit.textContent = original;
        return;
      }
      input.disabled = true;
      starButtons.forEach(s => s.disabled = true);
      submit.textContent = "✓ saved";
      if (data.newLesson) {
        showToast(`Learned: ${data.newLesson.lessonText}`);
      } else {
        showToast("Saved. No new lesson distilled this time.");
      }
      await refreshLessons();
    } catch (e) {
      showToast(`Feedback failed: ${e.message}`);
      submit.disabled = false;
      submit.textContent = "Teach";
    }
  };
  submit.addEventListener("click", send);
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      send();
    }
  });

  wrap.appendChild(input);
  wrap.appendChild(submit);
  return wrap;
}

function paintStars(buttons, upTo) {
  for (const b of buttons) {
    const v = Number(b.dataset.value);
    b.classList.toggle("filled", v <= upTo);
  }
}

async function loadHealth() {
  try {
    const response = await fetch("/api/health");
    const data = await response.json();
    healthDot?.classList.toggle("ok", Boolean(data.ok));
    if (docCount) docCount.textContent = data.mongo?.documentCount?.toLocaleString() ?? "Unknown";
    if (retrievalMode) retrievalMode.textContent = data.mongo?.retrievalMode ?? "Unknown";
    if (llmStatus) llmStatus.textContent = data.claudeConfigured ? "Claude configured" : "Claude key missing";
  } catch {
    healthDot?.classList.remove("ok");
    if (docCount) docCount.textContent = "Unavailable";
    if (retrievalMode) retrievalMode.textContent = "Unavailable";
    if (llmStatus) llmStatus.textContent = "Unavailable";
  }
}

async function refreshLessons() {
  try {
    const res = await fetch("/api/lessons");
    const data = await res.json();
    renderLessons(data.lessons ?? []);
  } catch {
    /* ignore */
  }
}

function renderLessons(list) {
  lessonsCount.textContent = list.length;
  lessonsList.innerHTML = "";
  if (list.length === 0) {
    const empty = document.createElement("div");
    empty.className = "lessonEmpty";
    empty.textContent = "No lessons yet. Ask a question, rate the answer (with optional notes), and watch one appear.";
    lessonsList.appendChild(empty);
    return;
  }
  for (const lesson of list) {
    const card = document.createElement("div");
    card.className = "lessonCard";
    if (lastAppliedLessonIds.has(lesson.id)) card.classList.add("applied");

    const h = document.createElement("h4");
    h.textContent = lesson.questionPattern;
    card.appendChild(h);

    const text = document.createElement("p");
    text.className = "lessonText";
    text.textContent = lesson.lessonText;
    card.appendChild(text);

    const meta = document.createElement("div");
    meta.className = "lessonMeta";
    for (const term of (lesson.suggestedQueryTerms ?? []).slice(0, 6)) {
      const t = document.createElement("span");
      t.className = "tag";
      t.textContent = term;
      meta.appendChild(t);
    }
    for (const st of lesson.suggestedSourceTypes ?? []) {
      const t = document.createElement("span");
      t.className = "tag";
      t.textContent = `type:${st}`;
      meta.appendChild(t);
    }
    card.appendChild(meta);

    const stats = document.createElement("div");
    stats.className = "lessonStats";
    const avg = (lesson.avgScoreWhenApplied || 0).toFixed(2);
    stats.textContent = `applied ${lesson.appliedCount}× · avg score when applied ${avg} · ${lesson.feedbackCount ?? 0} feedback`;
    card.appendChild(stats);

    if (lesson.sampleFeedback) {
      const fb = document.createElement("div");
      fb.className = "lessonFeedback";
      fb.textContent = `“${lesson.sampleFeedback}”`;
      card.appendChild(fb);
    }

    lessonsList.appendChild(card);
  }
}

lessonsToggle.addEventListener("change", () => {
  const on = lessonsToggle.checked;
  lessonsToggleLabel.textContent = on ? "ON" : "OFF";
  lessonsToggle.parentElement.classList.toggle("off", !on);
});

complaintsToggle.addEventListener("change", () => {
  const on = complaintsToggle.checked;
  complaintsToggleLabel.textContent = on ? "ON" : "OFF";
  complaintsToggle.parentElement.classList.toggle("off", !on);
});

learnBtn.addEventListener("click", async () => {
  learnBtn.disabled = true;
  learnBtn.textContent = "Learning…";
  try {
    const res = await fetch("/api/learn-from-history", { method: "POST" });
    const data = await res.json();
    showToast(`Backfilled ${data.lessonsCreated} lesson${data.lessonsCreated === 1 ? "" : "s"} from history.`);
    await refreshLessons();
  } catch (e) {
    showToast(`Learn failed: ${e.message}`);
  } finally {
    learnBtn.disabled = false;
    learnBtn.textContent = "Learn from history";
  }
});

// Enter submits; Shift+Enter inserts a newline (preserves multiline input for those who want it).
question.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    form.requestSubmit();
  }
});

function addThinkingBubble() {
  const node = document.createElement("div");
  node.className = "message assistant thinking";
  node.innerHTML = '<p><span class="thinkingDot"></span><span class="thinkingDot"></span><span class="thinkingDot"></span> <em>Claude is thinking…</em></p>';
  messages.appendChild(node);
  messages.scrollTop = messages.scrollHeight;
  return node;
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const text = question.value.trim();
  if (!text) return;

  addMessage("user", text);
  question.value = "";
  form.querySelector("button").disabled = true;
  const thinking = addThinkingBubble();

  try {
    const response = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        question: text,
        limit: 8,
        useLessons: lessonsToggle.checked,
        useComplaints: complaintsToggle.checked
      })
    });

    const data = await response.json();
    thinking.remove();
    if (!response.ok) {
      addMessage("assistant", data.error ?? "The request failed.");
    } else {
      lastAppliedLessonIds = new Set((data.appliedLessons ?? []).map(l => l.id));
      addMessage("assistant", data.answer, {
        sources: data.sources ?? [],
        complaints: data.complaints ?? [],
        chatId: data.chatId,
        topScore: data.topScore,
        avgScoreBaseline: data.avgScoreBaseline,
        appliedLessons: data.appliedLessons ?? [],
        usedLessonsMode: data.usedLessonsMode,
        searchQueries: data.searchQueries ?? []
      });
      if (data.primaryLocation) dropPinFor(data.primaryLocation);
      if (data.newLesson) {
        showToast(`Learned: ${data.newLesson.lessonText}`);
      }
      await refreshLessons();
    }
  } catch (error) {
    thinking.remove();
    addMessage("assistant", `Network error: ${error.message}`);
  } finally {
    form.querySelector("button").disabled = false;
    question.focus();
  }
});

loadHealth();
refreshLessons();
initMap();
