(function() {
  function computeSwaggerUrl() {
    try {
      var base = window.location.pathname.replace(/\/swagger(\/index\.html)?$/, '');
      return window.location.origin + base + '/swagger/v1/swagger.json';
    } catch (e) { return '/swagger/v1/swagger.json'; }
  }
  var _orig = JSON.parse;
  JSON.parse = function(text) {
    var result = _orig.apply(this, arguments);
    try {
      if (result && Array.isArray(result.urls) && result.urls.length > 0 &&
          result.urls[0].url && result.urls[0].url.indexOf('swagger.json') !== -1) {
        var correctUrl = computeSwaggerUrl();
        result.urls = result.urls.map(function(item) {
          if (item.url && item.url.indexOf('swagger.json') !== -1) {
            return { url: correctUrl, name: item.name };
          }
          return item;
        });
      }
    } catch(e) {}
    return result;
  };
})();
// cb 1773274162
