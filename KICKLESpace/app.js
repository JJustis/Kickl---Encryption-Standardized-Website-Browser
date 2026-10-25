/* KICKLspace — small, dependency-free interactions. */
(function () {
  "use strict";

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ------------------------------------------------------------- sticky nav */
  var nav = document.getElementById("nav");
  var onScroll = function () {
    if (!nav) return;
    nav.classList.toggle("is-stuck", window.scrollY > 12);
  };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  /* ------------------------------------------------------------ mobile menu */
  var burger = document.getElementById("burger");
  var links = document.getElementById("navLinks");
  if (burger && links) {
    burger.addEventListener("click", function () {
      var open = links.classList.toggle("is-open");
      burger.setAttribute("aria-expanded", open ? "true" : "false");
    });
    links.addEventListener("click", function (event) {
      if (event.target.closest("a")) {
        links.classList.remove("is-open");
        burger.setAttribute("aria-expanded", "false");
      }
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && links.classList.contains("is-open")) {
        links.classList.remove("is-open");
        burger.setAttribute("aria-expanded", "false");
        burger.focus();
      }
    });
  }

  /* ---------------------------------------------------------- scroll reveal */
  var revealables = Array.prototype.slice.call(document.querySelectorAll(".reveal"));
  if (!("IntersectionObserver" in window) || reduceMotion) {
    revealables.forEach(function (el) { el.classList.add("is-visible"); });
  } else {
    var revealObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        var el = entry.target;
        var siblings = el.parentElement ? Array.prototype.slice.call(el.parentElement.children) : [];
        var index = siblings.indexOf(el);
        var delay = Math.max(0, Math.min(index, 6)) * 70;
        window.setTimeout(function () { el.classList.add("is-visible"); }, delay);
        revealObserver.unobserve(el);
      });
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
    revealables.forEach(function (el) { revealObserver.observe(el); });
  }

  /* ---------------------------------------------------------- stat counters */
  var counters = Array.prototype.slice.call(document.querySelectorAll("[data-count]"));
  var animateCounter = function (el) {
    var target = parseInt(el.getAttribute("data-count"), 10) || 0;
    if (reduceMotion || target === 0) {
      el.textContent = String(target);
      return;
    }
    var started = null;
    var duration = 1100;
    var step = function (timestamp) {
      if (started === null) started = timestamp;
      var progress = Math.min((timestamp - started) / duration, 1);
      var eased = 1 - Math.pow(1 - progress, 3);
      el.textContent = String(Math.round(target * eased));
      if (progress < 1) window.requestAnimationFrame(step);
      else el.textContent = String(target);
    };
    window.requestAnimationFrame(step);
  };

  if (!("IntersectionObserver" in window)) {
    counters.forEach(animateCounter);
  } else {
    var counterObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        animateCounter(entry.target);
        counterObserver.unobserve(entry.target);
      });
    }, { threshold: 0.4 });
    counters.forEach(function (el) { counterObserver.observe(el); });
  }

  /* -------------------------------------------------------------- copy hash */
  var toast = document.getElementById("toast");
  var toastTimer = null;
  var showToast = function (message) {
    if (!toast) return;
    toast.textContent = message;
    toast.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () {
      toast.classList.remove("is-visible");
    }, 2200);
  };

  var copy = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      try {
        document.execCommand("copy");
        resolve();
      } catch (error) {
        reject(error);
      } finally {
        document.body.removeChild(area);
      }
    });
  };

  Array.prototype.forEach.call(document.querySelectorAll("[data-copy]"), function (button) {
    button.addEventListener("click", function () {
      var source = document.querySelector(button.getAttribute("data-copy"));
      if (!source) return;
      var value = (source.textContent || "").trim();
      copy(value).then(function () {
        showToast("SHA-256 copied to the clipboard");
      }).catch(function () {
        showToast("Could not copy — select the hash and copy it manually");
      });
    });
  });

  /* ------------------------------------------------------------ mockup tilt */
  var mockup = document.getElementById("mockup");
  if (mockup && !reduceMotion && window.matchMedia("(hover: hover) and (pointer: fine)").matches) {
    var frame = null;
    var reset = function () {
      mockup.style.setProperty("--rx", "6deg");
      mockup.style.setProperty("--ry", "-8deg");
    };
    var stage = mockup.parentElement;
    stage.addEventListener("pointermove", function (event) {
      if (frame) return;
      frame = window.requestAnimationFrame(function () {
        frame = null;
        var rect = stage.getBoundingClientRect();
        var x = (event.clientX - rect.left) / rect.width - 0.5;
        var y = (event.clientY - rect.top) / rect.height - 0.5;
        mockup.style.setProperty("--ry", (x * 12 - 3).toFixed(2) + "deg");
        mockup.style.setProperty("--rx", (y * -8 + 3).toFixed(2) + "deg");
      });
    });
    stage.addEventListener("pointerleave", reset);
  }

  /* ------------------------------------------------------------------ year */
  var year = document.getElementById("year");
  if (year) year.textContent = String(new Date().getFullYear());
})();
