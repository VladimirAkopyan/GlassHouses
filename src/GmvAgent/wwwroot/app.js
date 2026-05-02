const messages = document.querySelector("#messages");
const form = document.querySelector("#chatForm");
const question = document.querySelector("#question");
const healthDot = document.querySelector("#healthDot");
const docCount = document.querySelector("#docCount");
const retrievalMode = document.querySelector("#retrievalMode");
const llmStatus = document.querySelector("#llmStatus");

function addMessage(role, text, sources = []) {
  const node = document.createElement("div");
  node.className = `message ${role}`;

  const body = document.createElement("p");
  body.textContent = text;
  node.appendChild(body);

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

  messages.appendChild(node);
  messages.scrollTop = messages.scrollHeight;
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
      body: JSON.stringify({ question: text, limit: 8 })
    });

    const data = await response.json();
    if (!response.ok) {
      addMessage("assistant", data.error ?? "The request failed.");
    } else {
      addMessage("assistant", data.answer, data.sources ?? []);
    }
  } catch (error) {
    addMessage("assistant", `Network error: ${error.message}`);
  } finally {
    form.querySelector("button").disabled = false;
    question.focus();
  }
});

loadHealth();
