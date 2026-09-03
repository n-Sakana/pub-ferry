"use strict";

var BUILD_ID = "__FERRY_BUILD_ID__";
var CACHE_NAME = "ferry-shell-" + BUILD_ID;
var SHELL = [
  "/",
  "/app.css?v=" + BUILD_ID,
  "/app.js?v=" + BUILD_ID,
  "/icon.svg?v=" + BUILD_ID,
  "/manifest.webmanifest?v=" + BUILD_ID
];

self.addEventListener("install", function (event) {
  event.waitUntil(
    caches.open(CACHE_NAME).then(function (cache) {
      return cache.addAll(SHELL);
    }).then(function () {
      return self.skipWaiting();
    })
  );
});

self.addEventListener("activate", function (event) {
  event.waitUntil(
    caches.keys().then(function (names) {
      return Promise.all(names.filter(function (name) {
        return name.indexOf("ferry-shell-") === 0 && name !== CACHE_NAME;
      }).map(function (name) {
        return caches.delete(name);
      }));
    }).then(function () {
      return self.clients.claim();
    })
  );
});

self.addEventListener("fetch", function (event) {
  var request = event.request;
  if (request.method !== "GET") {
    return;
  }
  var url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname.indexOf("/api/") === 0) {
    return;
  }

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request).catch(function () {
        return caches.match("/");
      })
    );
    return;
  }

  event.respondWith(
    caches.match(request).then(function (cached) {
      return cached || fetch(request);
    })
  );
});
