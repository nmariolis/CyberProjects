(function () {
  var DOCS_SESSION_KEY = "supplier_docs_session";
  var DOCS_LOGIN_PAGE = "./DocumentationLogin.html";
  var STORAGE_BASE = "supplierapi_custom_base";
  var STORAGE_KEY = "supplierapi_custom_key";
  var STORAGE_ACTIVE_HUB = "supplier_active_hub_id";
  var STORAGE_VIS_PREFIX = "supplier_endpoint_visibility::";

  var baseInput = document.getElementById("baseUrl");
  var keyInput = document.getElementById("apiKey");
  var sidebar = document.getElementById("sidebar-nav");
  var meta = document.getElementById("api-meta");
  var container = document.getElementById("endpoint-container");

  function getDocsSession() {
    try {
      var raw = sessionStorage.getItem(DOCS_SESSION_KEY);
      if (!raw) return null;
      var parsed = JSON.parse(raw);
      if (!parsed || !parsed.baseUrl || !parsed.integrationApiKey) return null;
      return parsed;
    } catch (e) {
      return null;
    }
  }

  var docsSession = getDocsSession();
  if (!docsSession) {
    window.location.replace(DOCS_LOGIN_PAGE);
    return;
  }

  baseInput.value = docsSession.baseUrl;
  keyInput.value = docsSession.integrationApiKey;
  baseInput.readOnly = true;
  keyInput.readOnly = true;
  baseInput.title = "Managed by Documentation Login";
  keyInput.title = "Managed by Documentation Login";
  localStorage.setItem(STORAGE_BASE, docsSession.baseUrl);
  localStorage.setItem(STORAGE_KEY, docsSession.integrationApiKey);
  localStorage.setItem(STORAGE_ACTIVE_HUB, docsSession.hubId || "default");

  baseInput.addEventListener("change", persistGlobals);
  keyInput.addEventListener("change", persistGlobals);

  function persistGlobals() {
    localStorage.setItem(STORAGE_BASE, baseInput.value.trim());
    localStorage.setItem(STORAGE_KEY, keyInput.value.trim());
  }

  function getActiveHubId() {
    return localStorage.getItem(STORAGE_ACTIVE_HUB) || "default";
  }

  function getVisibilityMap() {
    try {
      var raw = localStorage.getItem(STORAGE_VIS_PREFIX + getActiveHubId());
      if (!raw) return {};
      var parsed = JSON.parse(raw);
      return parsed && typeof parsed === "object" ? parsed : {};
    } catch (e) {
      return {};
    }
  }

  function endpointVisibilityKey(method, path) {
    return method.toUpperCase() + " " + path;
  }

  function isEndpointVisible(method, path, visibilityMap) {
    var value = visibilityMap[endpointVisibilityKey(method, path)];
    return value !== "hidden";
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/\"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function resolveRef(schema, schemas) {
    if (!schema || !schema.$ref) return schema;
    var match = schema.$ref.match(/#\/components\/schemas\/(.+)$/);
    if (!match) return schema;
    return schemas[match[1]] || schema;
  }

  function sampleFromSchema(schema, schemas, depth) {
    depth = depth || 0;
    if (depth > 4 || !schema) return null;

    var resolved = resolveRef(schema, schemas);
    if (resolved !== schema) {
      return sampleFromSchema(resolved, schemas, depth + 1);
    }

    if (schema.example !== undefined) return schema.example;

    if (schema.type === "object" || schema.properties) {
      var obj = {};
      var props = schema.properties || {};
      Object.keys(props).forEach(function (key) {
        obj[key] = sampleFromSchema(props[key], schemas, depth + 1);
      });
      return obj;
    }

    if (schema.type === "array") {
      return [sampleFromSchema(schema.items || {}, schemas, depth + 1)];
    }

    if (schema.enum && schema.enum.length) {
      return schema.enum[0];
    }

    if (schema.type === "string") {
      if (schema.format === "date-time") return "2026-01-01T00:00:00Z";
      if (schema.format === "date") return "2026-01-01";
      return "string";
    }

    if (schema.type === "integer") return 0;
    if (schema.type === "number") return 0;
    if (schema.type === "boolean") return false;

    return null;
  }

  function schemaType(schema, schemas) {
    var resolved = resolveRef(schema, schemas) || {};
    if (resolved.type) return resolved.type;
    if (resolved.properties) return "object";
    if (resolved.items) return "array";
    return "string";
  }

  function setDeep(target, path, value) {
    var ref = target;
    for (var i = 0; i < path.length - 1; i++) {
      var key = path[i];
      if (ref[key] === undefined || ref[key] === null || typeof ref[key] !== "object" || Array.isArray(ref[key])) {
        ref[key] = {};
      }
      ref = ref[key];
    }
    ref[path[path.length - 1]] = value;
  }

  function parseInputValue(rawValue, schema, schemas) {
    var resolved = resolveRef(schema, schemas) || {};
    var type = schemaType(resolved, schemas);

    if (rawValue === "") {
      return { hasValue: false };
    }

    if (type === "integer") {
      var nInt = Number(rawValue);
      if (!Number.isFinite(nInt) || Math.floor(nInt) !== nInt) {
        return { error: "Expected integer" };
      }
      return { hasValue: true, value: nInt };
    }

    if (type === "number") {
      var n = Number(rawValue);
      if (!Number.isFinite(n)) {
        return { error: "Expected number" };
      }
      return { hasValue: true, value: n };
    }

    if (type === "boolean") {
      var v = rawValue.toLowerCase();
      if (v === "true" || v === "1" || v === "yes") {
        return { hasValue: true, value: true };
      }
      if (v === "false" || v === "0" || v === "no") {
        return { hasValue: true, value: false };
      }
      return { error: "Use true/false" };
    }

    if (type === "array" || type === "object") {
      try {
        return { hasValue: true, value: JSON.parse(rawValue) };
      } catch (e) {
        return { error: "Invalid JSON" };
      }
    }

    return { hasValue: true, value: rawValue };
  }

  function bodyFieldPlaceholder(schema, schemas) {
    var resolved = resolveRef(schema, schemas) || {};
    var type = schemaType(resolved, schemas);
    if (type === "string") {
      if (resolved.format === "date-time") return "2026-01-01T00:00:00Z";
      if (resolved.format === "date") return "2026-01-01";
      return "text";
    }
    if (type === "integer") return "0";
    if (type === "number") return "0.00";
    if (type === "boolean") return "true/false";
    if (type === "array") return "[ ... ]";
    if (type === "object") return "{ ... }";
    return "value";
  }

  function inputConfigForSchema(schema, schemas) {
    var resolved = resolveRef(schema, schemas) || {};
    var type = schemaType(resolved, schemas);

    if (type === "string" && resolved.format === "date") {
      return { inputType: "date", cssClass: "body-input" };
    }

    if (type === "string" && resolved.format === "date-time") {
      return { inputType: "datetime-local", cssClass: "body-input" };
    }

    if (type === "array" || (type === "object" && !resolved.properties)) {
      return { inputType: "textarea", cssClass: "body-input body-input-json" };
    }

    return { inputType: "text", cssClass: "body-input" };
  }

  function buildBodyForm(schema, schemas, idPrefix) {
    var resolved = resolveRef(schema, schemas) || {};
    var required = Array.isArray(resolved.required) ? resolved.required : [];
    var fields = [];
    var nodeCounter = 0;

    function walk(nodeSchema, path, parentRequired, depth) {
      var node = resolveRef(nodeSchema, schemas) || {};
      var nodeType = schemaType(node, schemas);

      if (depth > 5) {
        return '<div class="form-note">Depth limit reached for nested schema.</div>';
      }

      if (nodeType !== "object") {
        return '<div class="form-note">Request body is not an object. Use raw JSON body editor.</div>';
      }

      var props = node.properties || {};
      var keys = Object.keys(props);

      if (!keys.length) {
        return '<div class="form-note">No properties found for this schema.</div>';
      }

      var html = '<div class="body-form-group">';

      keys.forEach(function (propName) {
        var propSchema = props[propName];
        var resolvedProp = resolveRef(propSchema, schemas) || {};
        var propType = schemaType(resolvedProp, schemas);
        var isRequired = parentRequired.indexOf(propName) >= 0;
        var fullPath = path.concat([propName]);

        if (propType === "object" && resolvedProp.properties) {
          var childRequired = Array.isArray(resolvedProp.required) ? resolvedProp.required : [];
          html += '<fieldset class="body-fieldset">';
          html += '<legend>' + escapeHtml(propName + (isRequired ? " *" : "")) + '</legend>';
          html += walk(resolvedProp, fullPath, childRequired, depth + 1);
          html += '</fieldset>';
          return;
        }

        var inputId = idPrefix + "-body-field-" + nodeCounter++;
        var hint = propType;
        if (resolvedProp.format) {
          hint += " / " + resolvedProp.format;
        }

        var inputCfg = inputConfigForSchema(resolvedProp, schemas);

        fields.push({
          inputId: inputId,
          keyPath: fullPath,
          schema: resolvedProp,
          required: isRequired,
          label: propName,
          inputType: inputCfg.inputType
        });

        if (inputCfg.inputType === "textarea") {
          var example = sampleFromSchema(resolvedProp, schemas, 0);
          var jsonDefault = example === null || example === undefined ? "" : JSON.stringify(example, null, 2);
          html += '<div class="param-row body-param-row">';
          html += '<div class="param-label">' + escapeHtml(propName + (isRequired ? " *" : "")) + '<br><small>' + escapeHtml(hint) + '</small></div>';
          html += '<textarea class="body-input body-input-json" id="' + inputId + '" placeholder="' + escapeHtml(bodyFieldPlaceholder(resolvedProp, schemas)) + '">' + escapeHtml(jsonDefault) + '</textarea>';
          html += '</div>';
          return;
        }

        html += '<div class="param-row body-param-row">';
        html += '<div class="param-label">' + escapeHtml(propName + (isRequired ? " *" : "")) + '<br><small>' + escapeHtml(hint) + '</small></div>';
        html += '<input class="' + inputCfg.cssClass + '" id="' + inputId + '" type="' + inputCfg.inputType + '" placeholder="' + escapeHtml(bodyFieldPlaceholder(resolvedProp, schemas)) + '" />';
        html += '</div>';
      });

      html += '</div>';
      return html;
    }

    return {
      fields: fields,
      html: walk(resolved, [], required, 0)
    };
  }

  function createParamInput(param, idx, pIdx) {
    var inputId = "param-" + idx + "-" + pIdx;
    var requiredTag = param.required ? " *" : "";
    var pType = (param.schema && param.schema.type) || "string";
    var pFormat = param.schema && param.schema.format ? param.schema.format : "";
    var inputType = "text";

    if (pType === "string" && pFormat === "date") {
      inputType = "date";
    }

    if (pType === "string" && pFormat === "date-time") {
      inputType = "datetime-local";
    }

    var placeholder = param.required ? "required" : "optional";
    var hint = pFormat ? pType + " / " + pFormat : pType;

    return {
      id: inputId,
      html:
        '<div class="param-row">' +
        '<div class="param-label">' +
        escapeHtml(param.name + requiredTag) +
        '<br><small>' +
        escapeHtml(param.in + " / " + hint) +
        "</small></div>" +
        '<input id="' +
        inputId +
        '" type="' +
        inputType +
        '" placeholder="' +
        placeholder +
        '" />' +
        "</div>"
    };
  }

  function buildEndpointCard(path, method, op, index, schemas) {
    var cardId = "ep-" + index;
    var methodClass = method.toLowerCase() === "post" ? "method-post" : "method-get";

    var pathParams = (op.parameters || []).slice();
    var paramInputs = [];
    var paramHtml = "";

    pathParams.forEach(function (param, pIdx) {
      if (param.in === "header" && param.name === "IntegrationApiKey") return;
      var input = createParamInput(param, index, pIdx);
      paramInputs.push({
        source: param,
        inputId: input.id
      });
      paramHtml += input.html;
    });

    if (!paramHtml) {
      paramHtml = '<div class="empty">No explicit params for this endpoint.</div>';
    }

    var reqBodySchema = null;
    var reqBodySample = "";
    var bodyForm = { html: "", fields: [] };
    if (op.requestBody && op.requestBody.content) {
      var bodyContent = op.requestBody.content["application/json"] || op.requestBody.content["text/json"];
      if (bodyContent && bodyContent.schema) {
        reqBodySchema = bodyContent.schema;
        var sample = sampleFromSchema(reqBodySchema, schemas, 0);
        reqBodySample = JSON.stringify(sample, null, 2);
        bodyForm = buildBodyForm(reqBodySchema, schemas, "ep-" + index);
      }
    }

    var tag = (op.tags && op.tags[0]) || "General";
    var summary = op.summary || op.description || "No description provided in spec.";
    var bodyId = "body-" + index;
    var runId = "run-" + index;
    var statusId = "status-" + index;
    var reqUrlId = "req-url-" + index;
    var respId = "resp-" + index;
    var copyId = "copy-" + index;
    var formSyncStatusId = "form-sync-status-" + index;

    var paramPanelContent = paramHtml;
    if (bodyForm.fields.length) {
      paramPanelContent +=
        '<div class="body-form-separator"></div>' +
        '<h4 class="sub-panel-title">Body Parameters</h4>' +
        '<div class="body-form-note">Fill the form and the Request Body JSON will update automatically.</div>' +
        bodyForm.html +
        '<div class="body-sync-status" id="' + formSyncStatusId + '"></div>';
    }

    var bodyPanelHtml = "";
    if (method.toLowerCase() === "post") {
      bodyPanelHtml =
        '<div class="panel">' +
        "<h4>Request Body</h4>" +
        '<textarea id="' +
        bodyId +
        '">' +
        escapeHtml(bodyForm.fields.length ? "{}" : reqBodySample || "{}") +
        "</textarea>" +
        "</div>";
    } else {
      bodyPanelHtml =
        '<div class="panel">' +
        "<h4>Request Body</h4>" +
        '<div class="empty">GET endpoints do not send a JSON body.</div>' +
        "</div>";
    }

    var html =
      '<article class="endpoint-card" id="' +
      cardId +
      '">' +
      '<div class="endpoint-head">' +
      '<div class="endpoint-main">' +
      '<span class="method-badge ' +
      methodClass +
      '">' +
      method.toUpperCase() +
      "</span>" +
      '<span class="endpoint-path">' +
      escapeHtml(path) +
      "</span>" +
      "</div>" +
      '<span class="tag">' +
      escapeHtml(tag) +
      "</span>" +
      "</div>" +
      '<div class="endpoint-body">' +
      "<p>" +
      escapeHtml(summary) +
      "</p>" +
      '<div class="grid-two">' +
      '<div class="panel"><h4>Parameters</h4>' +
      paramPanelContent +
      "</div>" +
      bodyPanelHtml +
      "</div>" +
      '<button class="run" id="' +
      runId +
      '"><i class="fa fa-play" aria-hidden="true"></i> Execute Request</button>' +
      '<div class="req-url" id="' +
      reqUrlId +
      '"></div>' +
      '<div class="status" id="' +
      statusId +
      '"></div>' +
      '<div class="response-head"><span>Response</span><button class="copy-response" id="' +
      copyId +
      '" type="button">Copy</button></div>' +
      '<pre class="response" id="' +
      respId +
      '"></pre>' +
      "</div>" +
      "</article>";

    return {
      id: cardId,
      path: path,
      method: method,
      html: html,
      bind: function () {
        var button = document.getElementById(runId);
        var statusEl = document.getElementById(statusId);
        var reqUrlEl = document.getElementById(reqUrlId);
        var respEl = document.getElementById(respId);
        var copyEl = document.getElementById(copyId);
        var bodyEl = document.getElementById(bodyId);
        var formSyncStatusEl = document.getElementById(formSyncStatusId);

        async function copyResponseText() {
          var text = respEl ? respEl.textContent || "" : "";
          if (!text) return;

          try {
            await navigator.clipboard.writeText(text);
          } catch (e) {
            var tmp = document.createElement("textarea");
            tmp.value = text;
            document.body.appendChild(tmp);
            tmp.select();
            document.execCommand("copy");
            document.body.removeChild(tmp);
          }

          copyEl.textContent = "Copied";
          setTimeout(function () {
            copyEl.textContent = "Copy";
          }, 1200);
        }

        if (copyEl) {
          copyEl.addEventListener("click", copyResponseText);
        }

        function syncBodyFromForm() {
          if (!bodyEl || !bodyForm.fields.length) return;

          var payload = {};
          var hasAnyValue = false;
          var hasError = false;

          bodyForm.fields.forEach(function (field) {
            var el = document.getElementById(field.inputId);
            if (!el) return;

            var raw = (el.value || "").trim();
            if (field.inputType === "datetime-local" && raw) {
              var dt = new Date(raw);
              raw = Number.isNaN(dt.getTime()) ? raw : dt.toISOString();
            }
            var parsed = parseInputValue(raw, field.schema, schemas);

            el.classList.remove("input-error");

            if (parsed.error) {
              hasError = true;
              el.classList.add("input-error");
              return;
            }

            if (!parsed.hasValue) {
              return;
            }

            setDeep(payload, field.keyPath, parsed.value);
            hasAnyValue = true;
          });

          bodyEl.value = hasAnyValue ? JSON.stringify(payload, null, 2) : "{}";

          if (formSyncStatusEl) {
            if (hasError) {
              formSyncStatusEl.textContent = "Some body fields have invalid format and were skipped.";
              formSyncStatusEl.className = "body-sync-status error";
            } else {
              formSyncStatusEl.textContent = "Request Body synchronized from form.";
              formSyncStatusEl.className = "body-sync-status ok";
            }
          }
        }

        if (bodyForm.fields.length) {
          bodyForm.fields.forEach(function (field) {
            var el = document.getElementById(field.inputId);
            if (!el) return;
            el.addEventListener("input", syncBodyFromForm);
            el.addEventListener("change", syncBodyFromForm);
          });

          syncBodyFromForm();
        }

        button.addEventListener("click", async function () {
          persistGlobals();

          try {
            var base = baseInput.value.trim().replace(/\/$/, "");
            if (!base) throw new Error("Base URL is required.");

            var finalPath = path;
            var query = [];
            var headers = {
              Accept: "application/json"
            };

            if (path.indexOf("/integrations/supplier-api/") === 0) {
              var key = keyInput.value.trim();
              if (key) {
                headers.IntegrationApiKey = key;
              }
            }

            paramInputs.forEach(function (item) {
              var value = (document.getElementById(item.inputId).value || "").trim();
              var param = item.source;
              var schema = param.schema || {};

              if (schema.type === "string" && schema.format === "date-time" && value) {
                var d = new Date(value);
                value = Number.isNaN(d.getTime()) ? value : d.toISOString();
              }

              if (param.required && !value) {
                throw new Error("Missing required parameter: " + param.name);
              }

              if (!value) return;

              if (param.in === "path") {
                finalPath = finalPath.replace("{" + param.name + "}", encodeURIComponent(value));
              }

              if (param.in === "query") {
                query.push(encodeURIComponent(param.name) + "=" + encodeURIComponent(value));
              }

              if (param.in === "header") {
                headers[param.name] = value;
              }
            });

            var url = base + finalPath + (query.length ? "?" + query.join("&") : "");
            reqUrlEl.textContent = "Request URL: " + url;

            var options = {
              method: method.toUpperCase(),
              headers: headers
            };

            if (method.toLowerCase() === "post") {
              var bodyText = bodyEl ? bodyEl.value.trim() : "";
              if (bodyText) {
                try {
                  JSON.parse(bodyText);
                } catch (err) {
                  throw new Error("Invalid JSON body.");
                }
                headers["Content-Type"] = "application/json";
                options.body = bodyText;
              }
            }

            statusEl.textContent = "Executing...";
            statusEl.className = "status";
            respEl.textContent = "";

            var proxyUrl = "/__proxy?target=" + encodeURIComponent(url);
            var response = await fetch(proxyUrl, options);
            var text = await response.text();
            var formatted = text;

            try {
              formatted = JSON.stringify(JSON.parse(text), null, 2);
            } catch (e) {
              formatted = text || "(empty response)";
            }

            statusEl.textContent = "Status: " + response.status + " " + (response.statusText || "");
            statusEl.className = response.ok ? "status ok" : "status error";
            respEl.textContent = formatted;
          } catch (err) {
            statusEl.textContent = err.message || "Request failed.";
            statusEl.className = "status error";
          }
        });
      }
    };
  }

  function render(spec) {
    var schemas = (spec.components && spec.components.schemas) || {};
    var info = spec.info || {};

    meta.textContent =
      "OpenAPI " +
      (spec.openapi || "") +
      " | " +
      (info.title || "SupplierAPI") +
      " | Version: " +
      (info.version || "-");

    var visibilityMap = getVisibilityMap();
    var entries = [];

    Object.keys(spec.paths || {}).forEach(function (path) {
      var pathItem = spec.paths[path] || {};
      ["get", "post", "put", "delete", "patch"].forEach(function (method) {
        if (pathItem[method] && isEndpointVisible(method, path, visibilityMap)) {
          entries.push({
            path: path,
            method: method,
            op: pathItem[method]
          });
        }
      });
    });

    if (!entries.length) {
      container.innerHTML = '<p class="empty">No endpoints found in swagger.json.</p>';
      return;
    }

    var endpointCards = [];
    var navHtml = "";
    var contentHtml = "";

    entries.forEach(function (entry, idx) {
      var card = buildEndpointCard(entry.path, entry.method, entry.op, idx, schemas);
      endpointCards.push(card);
      navHtml += '<li><a href="#' + card.id + '">' + entry.method.toUpperCase() + " " + escapeHtml(entry.path) + "</a></li>";
      contentHtml += card.html;
    });

    sidebar.innerHTML = navHtml;
    container.innerHTML = contentHtml;

    endpointCards.forEach(function (card) {
      card.bind();
    });

    var links = sidebar.querySelectorAll("a");
    links.forEach(function (link) {
      link.addEventListener("click", function () {
        links.forEach(function (l) {
          l.classList.remove("active");
        });
        link.classList.add("active");
      });
    });
  }

  fetch("../swagger.json", { cache: "no-store" })
    .then(function (response) {
      if (!response.ok) {
        throw new Error("Unable to load swagger.json");
      }
      return response.json();
    })
    .then(render)
    .catch(function (err) {
      meta.textContent = err.message || "Failed to load OpenAPI specification.";
      container.innerHTML = '<p class="empty">Cannot build interactive documentation.</p>';
    });
})();









