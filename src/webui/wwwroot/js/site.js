document.querySelectorAll("[data-prompt]").forEach((button) => {
  button.addEventListener("click", () => {
    const input = document.querySelector("#Input_UserMessage");
    if (!input) return;

    input.value = button.dataset.prompt ?? "";
    input.focus();
  });
});

document.querySelectorAll("[data-loading-form]").forEach((form) => {
  form.addEventListener("submit", () => {
    if (form.checkValidity()) {
      form.classList.add("is-loading");
    }
  });
});

const evidenceInput = document.querySelector("#Input_EvidenceFiles");
const selectedFiles = document.querySelector("[data-selected-files]");
if (evidenceInput && selectedFiles) {
  evidenceInput.addEventListener("change", () => {
    selectedFiles.replaceChildren();
    Array.from(evidenceInput.files ?? []).forEach((file) => {
      const item = document.createElement("span");
      item.textContent = `${file.name} · ${(file.size / 1024 / 1024).toFixed(1)} MB`;
      selectedFiles.appendChild(item);
    });
  });
}
