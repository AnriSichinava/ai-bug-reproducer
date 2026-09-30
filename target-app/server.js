// Demo Cart App — a deliberately tiny, deliberately buggy target for the AI Bug
// Reproducer agent to investigate. Not a real product: in-memory storage, no
// framework on the client, just enough surface area to host one realistic bug.
const express = require('express');
const session = require('express-session');
const bcrypt = require('bcryptjs');
const path = require('node:path');

const app = express();
app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));
app.use(session({
  secret: 'demo-cart-app-session-secret',
  resave: false,
  saveUninitialized: false,
}));

const users = new Map(); // email -> { id, email, passwordHash }
const products = [
  { id: 1, name: 'Wireless Mouse', price: 25 },
  { id: 2, name: 'Mechanical Keyboard', price: 90 },
  { id: 3, name: 'USB-C Hub', price: 40 },
];
// The source of truth for what's actually in a user's cart, persisted across
// logout/login the way a real database would be.
const persistentCarts = new Map(); // userId -> [{ id, productId, name, price }]
let nextUserId = 1;
let nextCartItemId = 1;

function requireAuth(req, res, next) {
  if (!req.session.userId) {
    return res.status(401).json({ error: 'Not authenticated' });
  }
  next();
}

app.post('/api/register', async (req, res) => {
  const { email, password } = req.body ?? {};
  if (!email || !password) {
    return res.status(400).json({ error: 'email and password are required' });
  }
  if (users.has(email)) {
    return res.status(409).json({ error: 'A user with this email already exists' });
  }

  const user = { id: nextUserId++, email, passwordHash: await bcrypt.hash(password, 10) };
  users.set(email, user);
  persistentCarts.set(user.id, []);

  req.session.userId = user.id;
  // BUG: the session cart is always reset to empty on login/register, instead of
  // being rehydrated from persistentCarts. It silently drifts out of sync with the
  // real cart the moment anything is removed after a fresh login.
  req.session.cart = { items: [] };

  res.json({ email: user.email });
});

app.post('/api/login', async (req, res) => {
  const { email, password } = req.body ?? {};
  const user = users.get(email);
  if (!user || !(await bcrypt.compare(password, user.passwordHash))) {
    return res.status(401).json({ error: 'Invalid email or password' });
  }

  req.session.userId = user.id;
  // Same bug as register: this should rehydrate from persistentCarts.get(user.id).
  req.session.cart = { items: [] };

  res.json({ email: user.email });
});

app.post('/api/logout', (req, res) => {
  req.session.destroy(() => res.json({ ok: true }));
});

app.get('/api/me', requireAuth, (req, res) => {
  const user = [...users.values()].find((u) => u.id === req.session.userId);
  res.json({ email: user.email });
});

app.get('/api/products', (_req, res) => {
  res.json(products);
});

// The cart page reads from persistentCarts (the real data), which is why the
// removed-but-still-there item is visible again after logging back in.
app.get('/api/cart', requireAuth, (req, res) => {
  res.json(persistentCarts.get(req.session.userId) ?? []);
});

app.post('/api/cart/items', requireAuth, (req, res) => {
  const { productId } = req.body ?? {};
  const product = products.find((p) => p.id === productId);
  if (!product) {
    return res.status(404).json({ error: 'Product not found' });
  }

  const item = { id: nextCartItemId++, productId: product.id, name: product.name, price: product.price };
  persistentCarts.get(req.session.userId).push(item);
  req.session.cart.items.push(item);

  res.status(201).json(item);
});

// THE BUG: this reads from req.session.cart.items (populated only with items
// added *this session*) instead of persistentCarts (the real cart). After a
// fresh login the session cart is empty, so removing an item that's visibly in
// the cart on the page fails to find it, and the handler crashes reading a
// property off the resulting `undefined`.
app.delete('/api/cart/items/:id', requireAuth, (req, res) => {
  const id = Number(req.params.id);
  const idx = req.session.cart.items.findIndex((i) => i.id === id);
  const removed = req.session.cart.items.splice(idx, 1)[0];
  persistentCarts.set(
    req.session.userId,
    persistentCarts.get(req.session.userId).filter((i) => i.id !== id),
  );
  res.json({ removed: removed.name });
});

const port = process.env.PORT || 4000;
app.listen(port, () => console.log(`Demo Cart App listening on port ${port}`));
