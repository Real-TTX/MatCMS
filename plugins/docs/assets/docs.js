// MatCMS Docs: copy buttons on code blocks and the current heading in the table of contents. Everything works without it.
(function () {
  "use strict";
  function ready(fn) { if (document.readyState !== "loading") fn(); else document.addEventListener("DOMContentLoaded", fn); }
  ready(function () {
    // Copy buttons: drawn hidden by the server, shown only where the clipboard can be written.
    if (navigator.clipboard && window.isSecureContext) {
      document.querySelectorAll(".docs [data-docs-copy]").forEach(function (btn) {
        btn.hidden = false;
        var label = btn.textContent;
        btn.addEventListener("click", function () {
          var code = btn.parentElement.querySelector("pre code");
          if (!code) return;
          navigator.clipboard.writeText(code.textContent).then(function () {
            btn.textContent = btn.getAttribute("data-copied") || label;
            setTimeout(function () { btn.textContent = label; }, 1500);
          });
        });
      });
    }
    // The heading being read is marked in "On this page".
    var links = Array.prototype.slice.call(document.querySelectorAll(".docs-toc a[href^='#']"));
    if (!links.length || !("IntersectionObserver" in window)) return;
    var byId = {};
    links.forEach(function (a) { byId[decodeURIComponent(a.getAttribute("href").slice(1))] = a; });
    var visible = {};
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { visible[e.target.id] = e.isIntersecting; });
      var current = null;
      for (var i = 0; i < links.length; i++) {
        var id = decodeURIComponent(links[i].getAttribute("href").slice(1));
        if (visible[id]) { current = id; break; }
      }
      if (!current) return;
      links.forEach(function (a) { a.classList.remove("active"); });
      if (byId[current]) byId[current].classList.add("active");
    }, { rootMargin: "-80px 0px -60% 0px" });
    Object.keys(byId).forEach(function (id) { var h = document.getElementById(id); if (h) io.observe(h); });
  });
})();
