#!/usr/bin/env node
// Operator-only entry point. Do not call from an HTTP route or ordinary deployment.
import { DatabaseSync } from 'node:sqlite';
import { existsSync, realpathSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { prepareEnrollment, secret } from '../src/auth.mjs';

const args = process.argv.slice(2);
let database, replace = false, fromStdin = false;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--database') database = args[++i];
  else if (args[i] === '--replace-credential') replace = true;
  else if (args[i] === '--code-from-stdin') fromStdin = true;
  else throw new Error('Usage: enroll-owner.mjs --database <existing absolute SQLite path> [--replace-credential] [--code-from-stdin]');
}
let db;
try {
  if (!database || !isAbsolute(database) || !existsSync(database)) throw new Error('Choose the existing absolute reporting database path.');
  let token;
  if (fromStdin) {
    const chunks = []; let size = 0;
    for await (const chunk of process.stdin) { size += chunk.length; if (size > 100) throw new Error('Invalid enrollment code.'); chunks.push(chunk); }
    token = Buffer.concat(chunks).toString('utf8').trim();
  } else {
    if (!process.stdout.isTTY) throw new Error('Use a trusted interactive terminal, or pass a securely generated code with --code-from-stdin.');
    token = secret();
  }
  db = new DatabaseSync(realpathSync(database));
  db.exec('PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;');
  const store = { db, transaction(fn) { db.exec('BEGIN IMMEDIATE'); try { const result = fn(); db.exec('COMMIT'); return result; } catch (error) { db.exec('ROLLBACK'); throw error; } } };
  // A wrong database must never silently become a reporting service.
  if (!db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='reports'").get()) throw new Error('This is not an initialized reporting database.');
  prepareEnrollment(store, { token, replace });
  console.log(fromStdin ? 'Enrollment is ready. The code expires in ten minutes.' : `Enrollment code (valid for ten minutes):\n${token}`);
} catch (error) {
  console.error(error instanceof Error && !error.code ? error.message : 'Enrollment could not be prepared. Check database access.');
  process.exitCode = 1;
} finally { db?.close(); }
