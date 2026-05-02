const messages = document.querySelector("#messages");
const form = document.querySelector("#chatForm");
const question = document.querySelector("#question");
const healthDot = document.querySelector("#healthDot");
const docCount = document.querySelector("#docCount");
const retrievalMode = document.querySelector("#retrievalMode");
const llmStatus = document.querySelector("#llmStatus");
const lessonsToggle = document.querySelector("#lessonsToggle");
const lessonsToggleLabel = document.querySelector("#lessonsToggleLabel");
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

  const body = document.createElement("p");
  body.textContent = text;
  node.appendChild(body);

  const sources = extras.sources ?? [];
  if (sources.length > 0) {
    const sourceList = document.createElement("div");
    sourceList.className = "sources";
    for (const source of sources.slice(0, 6)) {
      const item = document.createElement("span");
      const page = source.page ? ` p.${source.page}` : "";
      item.textContent = `${source.sourceId}#${source.chunkIndex}${page} · score ${Number(source.score).toFixed(3)}`;
      sourceList.appendChild(item);
    }
    node.appendChild(sourceList);
  }

  if (role === "assistant" && extras.chatId) {
    node.appendChild(buildAnswerFooter(extras));
  }

  messages.appendChild(node);
  messages.scrollTop = messages.scrollHeight;
  return node;
}

function buildAnswerFooter(extras) {
  const footer = document.createElement("div");
  footer.className = "answerFooter";

  const top = Number(extras.topScore || 0);
  const baseline = Number(extras.avgScoreBaseline || 0);
  const lessonsApplied = extras.appliedLessons ?? [];

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

  const input = document.createElement("input");
  input.type = "text";
  input.placeholder = "Tell the agent what was helpful, missing, or wrong…";
  input.className = "feedbackInput";

  const submit = document.createElement("button");
  submit.type = "button";
  submit.textContent = "Teach";
  submit.title = "Submit feedback so the agent learns from this answer";
  submit.className = "feedbackSubmit";

  const send = async () => {
    const text = input.value.trim();
    if (!text) return;
    submit.disabled = true;
    submit.textContent = "…";
    try {
      const res = await fetch("/api/rate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ chatId, feedback: text })
      });
      const data = await res.json();
      if (!res.ok) {
        showToast(data.error ?? "Feedback failed.");
        submit.disabled = false;
        submit.textContent = "Teach";
        return;
      }
      input.disabled = true;
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

async function loadHealth() {
  try {
    const response = await fetch("/api/health");
    const data = await response.json();
    healthDot.classList.toggle("ok", Boolean(data.ok));
    docCount.textContent = data.mongo?.documentCount?.toLocaleString() ?? "Unknown";
    retrievalMode.textContent = data.mongo?.retrievalMode ?? "Unknown";
    llmStatus.textContent = data.claudeConfigured ? "Claude configured" : "Claude key missing";
  } catch {
    healthDot.classList.remove("ok");
    docCount.textContent = "Unavailable";
    retrievalMode.textContent = "Unavailable";
    llmStatus.textContent = "Unavailable";
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
    empty.textContent = "No lessons yet. Ask a question, give it a 👍, and watch one appear.";
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

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const text = question.value.trim();
  if (!text) return;

  addMessage("user", text);
  question.value = "";
  form.querySelector("button").disabled = true;

  try {
    const response = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        question: text,
        limit: 8,
        useLessons: lessonsToggle.checked
      })
    });

    const data = await response.json();
    if (!response.ok) {
      addMessage("assistant", data.error ?? "The request failed.");
    } else {
      lastAppliedLessonIds = new Set((data.appliedLessons ?? []).map(l => l.id));
      addMessage("assistant", data.answer, {
        sources: data.sources ?? [],
        chatId: data.chatId,
        topScore: data.topScore,
        avgScoreBaseline: data.avgScoreBaseline,
        appliedLessons: data.appliedLessons ?? [],
        usedLessonsMode: data.usedLessonsMode
      });
      if (data.newLesson) {
        showToast(`Learned: ${data.newLesson.lessonText}`);
      }
      await refreshLessons();
    }
  } catch (error) {
    addMessage("assistant", `Network error: ${error.message}`);
  } finally {
    form.querySelector("button").disabled = false;
    question.focus();
  }
});

loadHealth();
refreshLessons();
