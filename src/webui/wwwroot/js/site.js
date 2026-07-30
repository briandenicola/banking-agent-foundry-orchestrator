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
