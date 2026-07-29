mergeInto(LibraryManager.library, {
  LunilSetProbeMarker: function (markerPtr) {
    var marker = UTF8ToString(markerPtr);
    document.title = marker;
    document.documentElement.setAttribute('data-lunil-probe', marker);
  }
});
