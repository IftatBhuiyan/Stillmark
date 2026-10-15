(function () {
  "use strict";

  var header = document.getElementById("site-header");
  var menuButton = document.getElementById("menu-button");
  var mobileNav = document.getElementById("mobile-nav");
  var year = document.getElementById("year");

  function closeMenu() {
    if (!menuButton || !mobileNav) return;
    menuButton.setAttribute("aria-expanded", "false");
    mobileNav.hidden = true;
    var label = menuButton.querySelector(".sr-only");
    if (label) label.textContent = "Open navigation";
  }

  document.documentElement.classList.remove("no-js");
  closeMenu();

  if (header) {
    var updateHeader = function () {
      header.classList.toggle("is-scrolled", window.scrollY > 4);
    };
    window.addEventListener("scroll", updateHeader, { passive: true });
    updateHeader();
  }

  if (menuButton && mobileNav) {
    menuButton.addEventListener("click", function () {
      var willOpen = menuButton.getAttribute("aria-expanded") !== "true";
      menuButton.setAttribute("aria-expanded", String(willOpen));
      mobileNav.hidden = !willOpen;
      var label = menuButton.querySelector(".sr-only");
      if (label) label.textContent = willOpen ? "Close navigation" : "Open navigation";
      if (willOpen) {
        var firstLink = mobileNav.querySelector("a");
        if (firstLink) firstLink.focus();
      }
    });

    mobileNav.addEventListener("click", function (event) {
      if (event.target.closest("a")) closeMenu();
    });

    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && menuButton.getAttribute("aria-expanded") === "true") {
        closeMenu();
        menuButton.focus();
      }
    });

    window.addEventListener("resize", function () {
      if (window.matchMedia("(min-width: 1021px)").matches) closeMenu();
    });
  }

  if (year) year.textContent = String(new Date().getFullYear());
})();

(function () {
  const dropdowns = [...document.querySelectorAll('.nav-dropdown')];
  function closeOthers(except) { for (const item of dropdowns) if (item !== except) item.open = false; }
  for (const item of dropdowns) {
    item.addEventListener('toggle', () => { if (item.open) closeOthers(item); });
    item.addEventListener('focusout', event => { if (event.relatedTarget && !item.contains(event.relatedTarget)) item.open = false; });
    item.querySelector('summary').addEventListener('keydown', event => {
      if (event.key === 'ArrowDown') { event.preventDefault(); item.open = true; closeOthers(item); item.querySelector('a').focus(); }
    });
  }
  document.addEventListener('pointerdown', event => { if (!event.target.closest('.nav-dropdown')) closeOthers(); });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    const current = dropdowns.find(item => item.open);
    if (current) { current.open = false; current.querySelector('summary').focus(); }
  });
})();
