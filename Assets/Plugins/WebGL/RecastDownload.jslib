// Browser-side file save for WebGL builds.
//
// A WebGL build runs inside the browser sandbox and has no access to the local filesystem,
// so System.IO.File.WriteAllBytes silently does nothing there. This hands the bytes to the
// browser as a Blob and clicks a temporary link, which produces a normal "file downloaded"
// result for the viewer.
//
// The content type is worked out from the file extension rather than hardcoded. This began
// as a screenshot-only path with the type pinned to image/png, and it now carries the look
// file and the notes as well. A JSON file announced as a PNG still downloads, but the
// browser has been told something untrue, and some of them act on it: opening it in an image
// viewer, or appending .png to the name.

mergeInto(LibraryManager.library, {

  RecastDownloadFile: function (filenamePtr, dataPtr, dataLength) {
    var filename = UTF8ToString(filenamePtr);

    var dot = filename.lastIndexOf('.');
    var ext = dot >= 0 ? filename.substring(dot + 1).toLowerCase() : '';
    var types = {
      png:  'image/png',
      jpg:  'image/jpeg',
      jpeg: 'image/jpeg',
      json: 'application/json',
      txt:  'text/plain',
      csv:  'text/csv'
    };
    var type = types[ext] || 'application/octet-stream';

    // copy out of the Unity heap: the heap can move, the Blob must own its bytes
    var bytes = new Uint8Array(HEAPU8.buffer, dataPtr, dataLength).slice();
    var blob = new Blob([bytes], { type: type });
    var url = URL.createObjectURL(blob);

    var link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
  }

});
