"use strict";

const $ = (selector, root = document) => root.querySelector(selector);
const esc = (value) =>
    String(value ?? "").replace(
        /[&<>"']/g,
        (character) =>
            ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character],
    );
const money = (cents) =>
    new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        maximumFractionDigits: 2,
    }).format(cents / 100);
const number = (value) => new Intl.NumberFormat("en-US").format(value);
const date = (value) =>
    new Intl.DateTimeFormat("en-US", {
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
    }).format(new Date(value));
const icon = (name) => `<svg class="icon" aria-hidden="true"><use href="#icon-${name}"/></svg>`;
const state = {
    session: null,
    products: [],
    dashboard: null,
    orders: [],
    orderTotal: 0,
    activity: [],
    activityTotal: 0,
    search: "",
    stockFilter: "all",
    orderFilter: "",
    inventoryPage: 1,
    orderPage: 1,
    activityPage: 1,
};
let toastTimer;
let loadVersion = 0;
const isManager = () => state.session?.role === "Manager";
const view = () =>
    ["overview", "inventory", "orders", "activity"].includes(location.hash.slice(1))
        ? location.hash.slice(1)
        : "overview";

async function api(path, options = {}) {
    const response = await fetch(`/api${path}`, {
        ...options,
        headers: {
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": state.session?.csrfToken ?? "",
            ...options.headers,
        },
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => ({}));
        if (response.status === 401 && !path.startsWith("/auth/")) {
            state.session = await api("/session");
            showSession();
        }
        throw new Error(
            problem.detail ||
                problem.title ||
                {
                    403: "Your viewer account has read-only access.",
                    429: "Too many attempts. Wait a minute and try again.",
                }[response.status] ||
                "Something went wrong. Please try again.",
        );
    }
    return response.status === 204 ? null : response.json();
}

function notify(message, error = false) {
    clearTimeout(toastTimer);
    const toast = $("#toast");
    toast.textContent = message;
    toast.classList.toggle("error", error);
    toast.hidden = false;
    toastTimer = setTimeout(
        () => {
            toast.hidden = true;
        },
        error ? 8000 : 4500,
    );
}

function showSession() {
    const authenticated = state.session?.authenticated;
    $("#login-screen").hidden = authenticated;
    $("#workspace").hidden = !authenticated;
    $("#demo-login").hidden = !state.session?.demo;
    $("#demo-badge").hidden = !state.session?.demo;
    if (!authenticated) {
        if ($("#modal").open) $("#modal").close();
        return;
    }
    $("#account-name").textContent = isManager() ? "Manager" : "Viewer";
    $("#account-role").textContent = state.session.email;
    $("#account-avatar").textContent = isManager() ? "M" : "V";
    $(".workspace-label div").firstChild.textContent = state.session.demo
        ? "Demo workspace"
        : "Stockroom workspace";
}

async function signIn(email, password, button) {
    button.disabled = true;
    $("#login-error").textContent = "";
    try {
        await api("/auth/login", { method: "POST", body: JSON.stringify({ email, password }) });
        state.session = await api("/session");
        $("#password").value = "";
        state.dashboard = null;
        $("#page-content").innerHTML = '<p class="loading">Loading your workspace…</p>';
        showSession();
        await loadData();
    } catch (error) {
        $("#login-error").textContent = error.message;
    } finally {
        button.disabled = false;
    }
}

async function loadCatalog() {
    const first = await api("/products?pageSize=100");
    const products = [...first.items];
    for (let page = 2; products.length < first.total; page++) {
        const next = await api(`/products?pageSize=100&page=${page}`);
        if (!next.items.length) break;
        products.push(...next.items);
    }
    return products;
}

async function loadData() {
    const version = ++loadVersion;
    try {
        const [dashboard, products, orders, activity] = await Promise.all([
            api("/dashboard"),
            loadCatalog(),
            api(
                `/orders?page=${state.orderPage}&pageSize=10${state.orderFilter ? `&status=${state.orderFilter}` : ""}`,
            ),
            api(`/activity?page=${state.activityPage}&pageSize=10`),
        ]);
        if (version !== loadVersion || !state.session.authenticated) return;
        Object.assign(state, {
            dashboard,
            products,
            orders: orders.items,
            orderTotal: orders.total,
            activity: activity.items,
            activityTotal: activity.total,
        });
        $("#inventory-count").textContent = dashboard.totalProducts;
        $("#order-count").textContent = dashboard.openOrders;
        render();
    } catch (error) {
        if (state.session?.authenticated) {
            $("#page-content").innerHTML =
                `<div class="empty-state"><strong>Unable to load the workspace</strong>${esc(error.message)}<p><button class="button secondary" data-action="refresh">Try again</button></p></div>`;
            notify(error.message, true);
        }
    }
}

function heading(title, description, action = "order") {
    return `<div class="page-heading"><div><span class="heading-caption">INVENTORY & OPERATIONS</span><h1>${title}</h1><p>${description}</p></div><div class="button-row"><button class="button secondary" data-action="refresh">Refresh</button>${isManager() ? `<button class="button primary" data-action="${action === "product" ? "new-product" : "new-order"}">${icon("plus")}${action === "product" ? "Add product" : "Create order"}</button>` : '<span class="tag">Read-only workspace</span>'}</div></div>`;
}

function stat(label, value, note, symbol) {
    return `<div class="stat"><div class="stat-top">${label}<span class="stat-icon">${icon(symbol)}</span></div><div class="stat-value">${value}</div><div class="stat-note">${note}</div></div>`;
}

function stockBadge(product) {
    return product.available === 0
        ? '<span class="badge empty">Out of stock</span>'
        : product.available <= product.reorderPoint
          ? '<span class="badge low">Low stock</span>'
          : '<span class="badge">In stock</span>';
}

function productCell(product) {
    return `<div class="product-cell"><span class="product-art">${icon("box")}</span><div><span class="product-name">${esc(product.name)}</span><span class="product-sku">${esc(product.sku)}</span></div></div>`;
}

function inventoryTable(products, compact = false) {
    if (!products.length)
        return '<div class="empty-state"><strong>No products found</strong>Add a product or try a different search.</div>';
    return `<div class="table-scroll"><table><thead><tr><th>Product</th>${compact ? "" : '<th class="num">Unit price</th><th class="num">On hand</th>'}<th class="num">Available</th><th class="num">Reserved</th><th>Status</th>${!compact && isManager() ? '<th><span class="muted">Action</span></th>' : ""}</tr></thead><tbody>${products.map((product) => `<tr><td>${productCell(product)}</td>${compact ? "" : `<td class="num">${money(product.priceCents)}</td><td class="num">${number(product.onHand)}</td>`}<td class="num"><strong>${number(product.available)}</strong></td><td class="num muted">${number(product.reserved)}</td><td>${stockBadge(product)}</td>${!compact && isManager() ? `<td><button class="table-action" data-action="adjust" data-id="${product.id}">Adjust stock</button></td>` : ""}</tr>`).join("")}</tbody></table></div>`;
}

function overview() {
    const dashboard = state.dashboard;
    const counts = dashboard.orderCounts;
    const total = counts.reserved + counts.fulfilled + counts.cancelled;
    const low = state.products
        .filter((product) => product.available <= product.reorderPoint)
        .sort((a, b) => a.available - b.available);
    return `${heading("A little clarity. A lot of control.", "Here’s where your inventory stands. Let’s keep things moving.")}
      <div class="stats">${stat("Inventory value", money(dashboard.inventoryValueCents), "Current stock × unit selling price", "box")}${stat("Available units", number(dashboard.availableUnits), `<span class="positive">${number(dashboard.totalProducts)} products</span> in your catalog`, "grid")}${stat("Open orders", number(dashboard.openOrders), `${number(dashboard.reservedUnits)} units reserved for customers`, "orders")}${stat("Needs attention", number(dashboard.lowStockCount), `<span class="warning">Products at or below reorder point</span>`, "alert")}</div>
      <div class="overview-grid"><div><section class="card"><div class="card-header"><div><h2>Inventory at a glance</h2><div class="card-subtitle">Available stock, minus the guesswork.</div></div><a class="text-link" href="#inventory">View inventory ${icon("arrow")}</a></div>${inventoryTable(state.products.slice(0, 5), true)}<div class="table-footer"><span>Showing ${Math.min(5, dashboard.totalProducts)} of ${dashboard.totalProducts} products</span><span>Availability updates after every order</span></div></section>
      <section class="card"><div class="card-header"><div><h2>Recent orders</h2><div class="card-subtitle">From reserved to out the door.</div></div><a class="text-link" href="#orders">All orders ${icon("arrow")}</a></div>${
          state.orders.length
              ? state.orders
                    .slice(0, 3)
                    .map(
                        (order) =>
                            `<div class="recent-order"><div><strong>${esc(order.customer)}</strong><p>${order.id.slice(0, 8).toUpperCase()} · ${esc(date(order.createdAt))}</p></div><span class="badge ${order.status}">${order.status}</span><div class="order-right"><strong>${money(order.totalCents)}</strong><p>${number(order.lines.reduce((sum, line) => sum + line.quantity, 0))} units</p></div><button class="icon-button" data-action="order-detail" data-id="${order.id}" aria-label="View order for ${esc(order.customer)}">${icon("arrow")}</button></div>`,
                    )
                    .join("")
              : '<div class="empty-state">Your first order starts here.</div>'
      }</section>
      <div class="callout">${icon("activity")}<div><strong>A clear record, by design.</strong> Stock adjustments, reservations, and order changes are recorded automatically. <a class="text-link" href="#activity">View activity →</a></div></div></div>
      <div class="overview-side"><section class="card"><div class="card-header"><h2>Order pipeline</h2><span class="tag">ALL TIME</span></div><div class="pipeline"><div class="pipeline-total">${total}<small>total orders</small></div>${[
          ["Reserved", counts.reserved, ""],
          ["Fulfilled", counts.fulfilled, "green"],
          ["Cancelled", counts.cancelled, "grey"],
      ]
          .map(
              ([label, value, color]) =>
                  `<div class="pipeline-row"><div><span>${label}</span><strong>${value}</strong></div><meter class="${color}" value="${value}" min="0" max="${total || 1}" aria-label="${label}: ${value} of ${total} orders"></meter></div>`,
          )
          .join("")}</div></section>
      <section class="card"><div class="card-header"><div><h2>Keep an eye on these</h2><div class="card-subtitle">A heads-up before stock runs out.</div></div></div>${
          low.length
              ? low
                    .slice(0, 4)
                    .map(
                        (product) =>
                            `<div class="insight"><span class="alert-icon">${icon("alert")}</span><div><strong>${esc(product.name)}</strong><p>Reorder point: ${number(product.reorderPoint)} units</p></div><span class="qty">${number(product.available)} left</span></div>`,
                    )
                    .join("")
              : '<div class="empty-state">Everything is above its reorder point.</div>'
      }</section></div></div>`;
}

function pagination(page, total, target, size = 10) {
    const pages = Math.max(1, Math.ceil(total / size));
    return `<div class="table-footer"><span>${total ? (page - 1) * size + 1 : 0}–${Math.min(page * size, total)} of ${number(total)} results</span><div class="button-row"><button class="button secondary" data-action="page" data-target="${target}" data-page="${page - 1}" ${page <= 1 ? "disabled" : ""}>Previous</button><span>Page ${page} of ${pages}</span><button class="button secondary" data-action="page" data-target="${target}" data-page="${page + 1}" ${page >= pages ? "disabled" : ""}>Next</button></div></div>`;
}

function inventory() {
    return `${heading("A place for every product.", "Know what’s on hand, what’s spoken for, and what needs a restock.", "product")}<section class="card"><div class="toolbar"><div class="search-box">${icon("search")}<input type="search" id="inventory-search" aria-label="Search inventory" placeholder="Search products or SKU…" value="${esc(state.search)}"></div><select id="stock-filter" class="filter-select" aria-label="Filter stock status"><option value="all">All stock levels</option><option value="low" ${state.stockFilter === "low" ? "selected" : ""}>Low stock</option><option value="healthy" ${state.stockFilter === "healthy" ? "selected" : ""}>In stock</option></select></div><div id="inventory-results"></div></section>`;
}

function renderInventoryResults() {
    const term = state.search.trim().toLowerCase();
    const products = state.products.filter(
        (product) =>
            `${product.name} ${product.sku}`.toLowerCase().includes(term) &&
            (state.stockFilter === "all" ||
                (state.stockFilter === "low"
                    ? product.available <= product.reorderPoint
                    : product.available > product.reorderPoint)),
    );
    state.inventoryPage = Math.min(
        state.inventoryPage,
        Math.max(1, Math.ceil(products.length / 10)),
    );
    $("#inventory-results").innerHTML =
        inventoryTable(products.slice((state.inventoryPage - 1) * 10, state.inventoryPage * 10)) +
        pagination(state.inventoryPage, products.length, "inventory");
}

function orders() {
    return `${heading("Keep good things moving.", "Reserve inventory, fulfill orders, and release stock when plans change.")}<section class="card"><div class="toolbar"><h2>Customer orders</h2><select id="order-filter" class="filter-select" aria-label="Filter order status">${["", "Reserved", "Fulfilled", "Cancelled"].map((status) => `<option value="${status}" ${status === state.orderFilter ? "selected" : ""}>${status || "All statuses"}</option>`).join("")}</select></div>${state.orders.length ? `<div class="table-scroll"><table><thead><tr><th>Order</th><th>Customer</th><th>Status</th><th class="num">Units</th><th class="num">Total</th><th>Created</th><th>Details</th></tr></thead><tbody>${state.orders.map((order) => `<tr><td><strong>#${order.id.slice(0, 8).toUpperCase()}</strong></td><td>${esc(order.customer)}</td><td><span class="badge ${order.status}">${order.status}</span></td><td class="num">${number(order.lines.reduce((sum, line) => sum + line.quantity, 0))}</td><td class="num">${money(order.totalCents)}</td><td class="muted">${esc(date(order.createdAt))}</td><td><button class="table-action" data-action="order-detail" data-id="${order.id}">View order</button></td></tr>`).join("")}</tbody></table></div>` : '<div class="empty-state"><strong>No orders here yet</strong>Create an order or choose another status.</div>'}${pagination(state.orderPage, state.orderTotal, "order")}</section>`;
}

const delta = (value) =>
    `<span class="delta ${value > 0 ? "positive" : value < 0 ? "negative" : "muted"}">${value > 0 ? "+" : ""}${value === 0 ? "—" : number(value)}</span>`;

function activity() {
    return `${heading("Every change has a story.", "An audit trail of what changed, when it happened, and who made it happen.")}<section class="card"><div class="card-header"><div><h2>Stock activity</h2><div class="card-subtitle">Recorded in the same transaction as each inventory change.</div></div><span class="tag">${number(state.activityTotal)} EVENTS</span></div>${state.activity.length ? `<div class="table-scroll"><table><thead><tr><th>Event & product</th><th class="num">On hand Δ</th><th class="num">Reserved Δ</th><th>Reason</th><th>Actor & time</th></tr></thead><tbody>${state.activity.map((event) => `<tr><td><span class="activity-kind">${esc(event.kind)}</span><span class="product-sku">${esc(event.productName)} · ${esc(event.sku)}</span></td><td class="num">${delta(event.onHandDelta)}</td><td class="num">${delta(event.reservedDelta)}</td><td>${esc(event.reason)}</td><td>${esc(event.actor)}<span class="product-sku">${esc(date(event.createdAt))}</span></td></tr>`).join("")}</tbody></table></div>` : '<div class="empty-state">Inventory changes will appear here.</div>'}${pagination(state.activityPage, state.activityTotal, "activity")}</section>`;
}

function render() {
    if (!state.dashboard) return;
    const current = view();
    $("#breadcrumb").textContent = current[0].toUpperCase() + current.slice(1);
    document.title = `${$("#breadcrumb").textContent} · Stockroom`;
    document.querySelectorAll("[data-nav]").forEach((link) => {
        link.classList.toggle("active", link.dataset.nav === current);
        if (link.dataset.nav === current) link.setAttribute("aria-current", "page");
        else link.removeAttribute("aria-current");
    });
    $("#page-content").innerHTML = { overview, inventory, orders, activity }[current]();
    if (current === "inventory") renderInventoryResults();
}

function openModal(title, content, eyebrow = "WORKSPACE") {
    $("#modal-title").textContent = title;
    $("#modal-eyebrow").textContent = eyebrow;
    $("#modal-body").innerHTML = content;
    $("#modal").showModal();
}

const formActions = (label) =>
    `<p class="form-error" id="form-error" role="alert"></p><div class="modal-actions"><button type="button" class="button secondary modal-close">Cancel</button><button type="submit" class="button primary">${label}</button></div>`;

function newProduct() {
    openModal(
        "Make room for something new.",
        `<form id="product-form"><div class="form-grid"><div><label for="sku">SKU</label><input id="sku" name="sku" maxlength="32" pattern="[A-Za-z0-9](?:[A-Za-z0-9]|-){1,31}" placeholder="KB-009" required></div><div><label for="name">Product name</label><input id="name" name="name" minlength="2" maxlength="100" required></div><div><label for="price">Unit selling price (USD)</label><input id="price" name="price" type="number" min="0" max="1000000" step="0.01" required></div><div><label for="stock">Opening stock</label><input id="stock" name="stock" type="number" min="0" max="1000000" step="1" value="0" required></div><div><label for="reorder">Reorder point</label><input id="reorder" name="reorder" type="number" min="0" max="1000000" step="1" value="10" required></div></div><p class="form-help">SKUs are unique. Opening stock is recorded in the activity log.</p>${formActions("Add product")}</form>`,
        "ADD PRODUCT",
    );
}

function adjustStock(id) {
    const product = state.products.find((product) => product.id === id);
    if (!product) return;
    openModal(
        "Keep your stock accurate.",
        `<form id="adjust-form" data-id="${id}" data-version="${product.version}"><p class="form-help"><strong>${esc(product.name)}</strong> · ${esc(product.sku)}<br>${number(product.onHand)} on hand · ${number(product.reserved)} reserved · ${number(product.available)} available</p><label for="delta">Quantity change</label><input id="delta" name="delta" type="number" min="-1000000" max="1000000" step="1" placeholder="e.g. 20 to add or -5 to remove" required><label for="reason">Reason</label><input id="reason" name="reason" minlength="3" maxlength="200" placeholder="e.g. Restock from supplier" required><p class="form-help">Reserved units are protected. If inventory changes while you’re editing, refresh and try again.</p>${formActions("Save adjustment")}</form>`,
        "ADJUST INVENTORY",
    );
}

function newOrder() {
    if (!state.products.length) {
        notify("Add a product before creating an order.", true);
        return;
    }
    openModal(
        "Good things start here.",
        `<form id="order-form"><label for="customer">Customer or company</label><input id="customer" name="customer" minlength="2" maxlength="100" placeholder="e.g. Northstar Studio" required><label>Products & quantities</label><div id="order-lines"></div><button type="button" class="button secondary" data-action="add-line">${icon("plus")}Add another product</button><div class="order-total"><span>Order total</span><strong id="order-total">$0.00</strong></div><p class="form-help">Creating an order reserves available units. Stock leaves the shelf when you fulfill it. Cancelling releases the reservation.</p>${formActions("Reserve inventory")}</form>`,
        "CREATE ORDER",
    );
    addOrderLine();
}

function addOrderLine() {
    if (document.querySelectorAll(".line-item").length >= 25) return;
    const row = document.createElement("div");
    row.className = "line-item";
    row.innerHTML = `<select class="line-product" aria-label="Product" required><option value="">Choose a product</option>${state.products.map((product) => `<option value="${product.id}" ${product.available === 0 ? "disabled" : ""}>${esc(product.name)} · ${product.available} available · ${money(product.priceCents)}</option>`).join("")}</select><input class="line-quantity" type="number" aria-label="Quantity" value="1" min="1" max="10000" step="1" required><button type="button" class="icon-button" data-action="remove-line" aria-label="Remove product">×</button>`;
    $("#order-lines").append(row);
}

function updateOrderTotal() {
    let total = 0;
    document.querySelectorAll(".line-item").forEach((row) => {
        const product = state.products.find(
            (product) => product.id === $(".line-product", row).value,
        );
        if (product) total += product.priceCents * Number($(".line-quantity", row).value);
    });
    if ($("#order-total")) $("#order-total").textContent = money(total);
}

async function orderDetail(id) {
    const order = await api(`/orders/${id}`);
    openModal(
        `Order #${order.id.slice(0, 8).toUpperCase()}`,
        `<div class="order-detail-meta"><strong>${esc(order.customer)}</strong><br>Created ${esc(date(order.createdAt))} · <span class="badge ${order.status}">${order.status}</span></div>${order.lines.map((line) => `<div class="order-detail-line"><span>${esc(line.name)}<span class="product-sku">${esc(line.sku)} · ${line.quantity} × ${money(line.unitPriceCents)}</span></span><strong>${money(line.quantity * line.unitPriceCents)}</strong></div>`).join("")}<div class="order-total"><span>Order total</span><strong>${money(order.totalCents)}</strong></div>${order.status === "Reserved" && isManager() ? `<p class="form-help">Fulfill when the items leave your shelf. Cancel to release the reserved stock. Each action completes this order and cannot be reversed.</p><p id="form-error" class="form-error" role="alert"></p><div class="modal-actions"><button class="button danger" data-action="transition" data-id="${order.id}" data-status="cancel">Cancel order</button><button class="button primary" data-action="transition" data-id="${order.id}" data-status="fulfill">Confirm fulfillment</button></div>` : '<p class="form-help">This order is available for viewing. All changes appear in the activity log.</p>'}`,
        "ORDER DETAILS",
    );
}

async function submitForm(form, operation, message) {
    const button = $("[type=submit]", form);
    button.disabled = true;
    $("#form-error").textContent = "";
    try {
        await operation();
        $("#modal").close();
        notify(message);
        await loadData();
    } catch (error) {
        if ($("#form-error")) $("#form-error").textContent = error.message;
    } finally {
        button.disabled = false;
    }
}

document.addEventListener("submit", (event) => {
    const form = event.target;
    if (form.id === "login-form") {
        event.preventDefault();
        signIn($("#email").value, $("#password").value, $("[type=submit]", form));
        return;
    }
    if (!["product-form", "adjust-form", "order-form"].includes(form.id)) return;
    event.preventDefault();
    const data = new FormData(form);
    if (form.id === "product-form")
        submitForm(
            form,
            () =>
                api("/products", {
                    method: "POST",
                    body: JSON.stringify({
                        sku: data.get("sku"),
                        name: data.get("name"),
                        priceCents: Math.round(Number(data.get("price")) * 100),
                        onHand: Number(data.get("stock")),
                        reorderPoint: Number(data.get("reorder")),
                    }),
                }),
            "Product added. Opening inventory is on record.",
        );
    if (form.id === "adjust-form")
        submitForm(
            form,
            () =>
                api(`/products/${form.dataset.id}/adjustments`, {
                    method: "POST",
                    body: JSON.stringify({
                        delta: Number(data.get("delta")),
                        reason: data.get("reason"),
                        expectedVersion: Number(form.dataset.version),
                    }),
                }),
            "Stock adjusted. Every unit accounted for.",
        );
    if (form.id === "order-form")
        submitForm(
            form,
            async () => {
                const lines = [...document.querySelectorAll(".line-item")]
                    .map((row) => ({
                        productId: $(".line-product", row).value,
                        quantity: Number($(".line-quantity", row).value),
                    }))
                    .sort((a, b) => a.productId.localeCompare(b.productId));
                if (!lines.length) throw new Error("Add at least one product.");
                if (new Set(lines.map((line) => line.productId)).size !== lines.length)
                    throw new Error("Choose each product once and combine its quantity.");
                const body = JSON.stringify({ customer: data.get("customer").trim(), lines });
                let attempt;
                try {
                    attempt = JSON.parse(sessionStorage.getItem("stockroom.orderAttempt"));
                } catch {
                    /* Start a fresh attempt if stored data is invalid. */
                }
                if (attempt?.body !== body) attempt = { body, key: crypto.randomUUID() };
                sessionStorage.setItem("stockroom.orderAttempt", JSON.stringify(attempt));
                await api("/orders", {
                    method: "POST",
                    body,
                    headers: { "Idempotency-Key": attempt.key },
                });
                sessionStorage.removeItem("stockroom.orderAttempt");
            },
            "Order created. Inventory is reserved.",
        );
});

document.addEventListener("click", async (event) => {
    const button = event.target.closest("button");
    if (!button) return;
    if (button.classList.contains("modal-close")) {
        $("#modal").close();
        return;
    }
    if (button.dataset.demo) {
        const role = button.dataset.demo;
        await signIn(
            `${role}@stockroom.local`,
            role === "manager" ? "Demo.Manager42!" : "Demo.Viewer42!",
            button,
        );
        return;
    }
    try {
        if (button.id === "logout") {
            button.disabled = true;
            await api("/auth/logout", { method: "POST" });
            sessionStorage.removeItem("stockroom.orderAttempt");
            state.session = await api("/session");
            state.dashboard = null;
            state.orderFilter = "";
            state.orderPage = state.activityPage = 1;
            showSession();
            button.disabled = false;
        }
        switch (button.dataset.action) {
            case "refresh":
                await loadData();
                break;
            case "new-product":
                newProduct();
                break;
            case "adjust":
                adjustStock(button.dataset.id);
                break;
            case "new-order":
                newOrder();
                break;
            case "add-line":
                addOrderLine();
                break;
            case "remove-line":
                button.closest(".line-item").remove();
                updateOrderTotal();
                break;
            case "order-detail":
                await orderDetail(button.dataset.id);
                break;
            case "page":
                state[`${button.dataset.target}Page`] = Number(button.dataset.page);
                if (button.dataset.target === "inventory") renderInventoryResults();
                else await loadData();
                break;
            case "transition": {
                document.querySelectorAll("[data-action=transition]").forEach((element) => {
                    element.disabled = true;
                });
                try {
                    await api(`/orders/${button.dataset.id}/${button.dataset.status}`, {
                        method: "POST",
                    });
                    $("#modal").close();
                    notify(
                        button.dataset.status === "fulfill"
                            ? "Order fulfilled. Stock and activity updated."
                            : "Order cancelled. Reserved stock is available again.",
                    );
                    await loadData();
                } catch (error) {
                    if ($("#form-error")) $("#form-error").textContent = error.message;
                    document.querySelectorAll("[data-action=transition]").forEach((element) => {
                        element.disabled = false;
                    });
                }
                break;
            }
        }
    } catch (error) {
        button.disabled = false;
        notify(error.message, true);
    }
});

document.addEventListener("input", (event) => {
    if (event.target.id === "inventory-search") {
        state.search = event.target.value;
        state.inventoryPage = 1;
        renderInventoryResults();
    }
    if (event.target.closest("#order-lines")) updateOrderTotal();
});
document.addEventListener("change", (event) => {
    if (event.target.id === "stock-filter") {
        state.stockFilter = event.target.value;
        state.inventoryPage = 1;
        renderInventoryResults();
    }
    if (event.target.id === "order-filter") {
        state.orderFilter = event.target.value;
        state.orderPage = 1;
        loadData();
    }
});
window.addEventListener("hashchange", () => {
    if (view() === "overview" && (state.orderFilter || state.orderPage !== 1)) {
        state.orderFilter = "";
        state.orderPage = 1;
        loadData();
    } else render();
});

async function start() {
    $("#today").textContent = new Intl.DateTimeFormat("en-US", {
        weekday: "long",
        month: "short",
        day: "numeric",
        year: "numeric",
    }).format(new Date());
    try {
        state.session = await api("/session");
        showSession();
        if (state.session.authenticated) await loadData();
    } catch {
        $("#login-screen").hidden = false;
        $("#login-error").textContent =
            "Unable to connect to Stockroom. Start the server and refresh this page.";
    }
}
start();
