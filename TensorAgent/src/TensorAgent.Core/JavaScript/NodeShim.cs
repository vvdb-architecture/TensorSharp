// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// The part of the Node surface that is better written in JavaScript than in
/// host callbacks.
///
/// <para>
/// Everything with I/O in it — <c>fs</c>, <c>console</c>, <c>fetch</c> — has to be
/// a host function, because JavaScriptCore has no I/O to reach. Everything that is
/// only <i>shape</i> should not be: <c>Buffer</c> is a <c>Uint8Array</c> subclass,
/// and expressing a subclass through the C API means synthesising a prototype
/// chain by hand, while here it is one <c>class … extends</c>. The same goes for
/// the promise wrappers around the synchronous <c>fs</c> calls and for the
/// <c>Response</c> object <c>fetch</c> resolves with. Written in JavaScript they
/// are short enough to read, and the host keeps only the three byte conversions
/// they genuinely cannot do (UTF-8, base64, hex).
/// </para>
/// </summary>
internal static class NodeShim
{
    /// <summary>Where the host's byte helpers are hung before this script runs.</summary>
    internal const string HostObject = "__tensoragentHost";

    /// <summary>Where this script leaves the factories the host calls back into.</summary>
    internal const string ShimObject = "__tensoragentShim";

    internal const string Source = """
(function () {
  'use strict';
  const host = globalThis.__tensoragentHost;
  const encode = host.encode;
  const decode = host.decode;

  // Buffer. Node's is a Uint8Array subclass and code in the wild relies on that
  // (`buf instanceof Uint8Array`, `.set`, `.subarray`), so it is one here too.
  class Buffer extends Uint8Array {
    static from(value, encodingOrOffset, length) {
      if (typeof value === 'string') {
        return new Buffer(encode(value, encodingOrOffset || 'utf8'));
      }
      if (value instanceof ArrayBuffer) {
        const offset = encodingOrOffset === undefined ? 0 : encodingOrOffset;
        const count = length === undefined ? value.byteLength - offset : length;
        return new Buffer(new Uint8Array(value, offset, count));
      }
      if (ArrayBuffer.isView(value)) {
        return new Buffer(new Uint8Array(value.buffer, value.byteOffset, value.byteLength));
      }
      if (Array.isArray(value)) {
        return new Buffer(Uint8Array.from(value));
      }
      throw new TypeError('Buffer.from expects a string, Buffer, ArrayBuffer, TypedArray or Array');
    }
    static of() { return Buffer.from(Array.prototype.slice.call(arguments)); }
    static alloc(size, fill) {
      const buffer = new Buffer(size);
      if (fill !== undefined && fill !== 0) {
        buffer.fill(typeof fill === 'string' ? encode(fill, 'utf8')[0] : fill);
      }
      return buffer;
    }
    static allocUnsafe(size) { return new Buffer(size); }
    static isBuffer(value) { return value instanceof Buffer; }
    static byteLength(value, encoding) {
      if (typeof value === 'string') return encode(value, encoding || 'utf8').length;
      if (ArrayBuffer.isView(value)) return value.byteLength;
      if (value instanceof ArrayBuffer) return value.byteLength;
      throw new TypeError('Buffer.byteLength expects a string or a buffer');
    }
    static concat(list, totalLength) {
      let total = totalLength;
      if (total === undefined) {
        total = 0;
        for (const part of list) total += part.length;
      }
      const out = new Buffer(total);
      let offset = 0;
      for (const part of list) {
        if (offset >= total) break;
        const take = Math.min(part.length, total - offset);
        out.set(part.subarray(0, take), offset);
        offset += take;
      }
      return out;
    }
    toString(encoding, start, end) {
      const view = this.subarray(start === undefined ? 0 : start, end === undefined ? this.length : end);
      return decode(view, encoding || 'utf8');
    }
    toJSON() { return { type: 'Buffer', data: Array.from(this) }; }
    equals(other) {
      if (!other || other.length !== this.length) return false;
      for (let i = 0; i < this.length; i++) if (this[i] !== other[i]) return false;
      return true;
    }
    slice(start, end) { return new Buffer(this.subarray(start, end)); }
  }

  class TextEncoder {
    get encoding() { return 'utf-8'; }
    encode(input) { return encode(input === undefined ? '' : String(input), 'utf8'); }
  }

  class TextDecoder {
    constructor(label) {
      const name = String(label === undefined ? 'utf-8' : label).toLowerCase();
      if (name !== 'utf-8' && name !== 'utf8') {
        throw new RangeError("TextDecoder on this host decodes only 'utf-8', not '" + label + "'");
      }
      this.encoding = 'utf-8';
      this.fatal = false;
      this.ignoreBOM = false;
    }
    decode(input) {
      if (input === undefined || input === null) return '';
      if (input instanceof ArrayBuffer) return decode(new Uint8Array(input), 'utf8');
      return decode(input, 'utf8');
    }
  }

  const define = (name, value) =>
    Object.defineProperty(globalThis, name, { value, writable: true, configurable: true, enumerable: false });
  define('Buffer', Buffer);
  define('TextEncoder', TextEncoder);
  define('TextDecoder', TextDecoder);
  define('global', globalThis);

  const shim = {
    // The promise-shaped half of `fs`. The work is the same synchronous host call;
    // only the shape differs, which is exactly what a script's `await` needs.
    addFsPromises(fs) {
      const wrap = (fn) => function () {
        try { return Promise.resolve(fn.apply(fs, arguments)); }
        catch (error) { return Promise.reject(error); }
      };
      fs.promises = {
        readFile: wrap(fs.readFileSync),
        writeFile: wrap(fs.writeFileSync),
        appendFile: wrap(fs.appendFileSync),
        mkdir: wrap(fs.mkdirSync),
        readdir: wrap(fs.readdirSync),
        stat: wrap(fs.statSync),
        rm: wrap(fs.rmSync),
        access: function (target) {
          try {
            if (!fs.existsSync(target)) {
              const error = new Error("ENOENT: no such file or directory, access '" + target + "'");
              error.code = 'ENOENT';
              throw error;
            }
            return Promise.resolve();
          } catch (error) { return Promise.reject(error); }
        },
      };
      return fs;
    },

    makeStats(raw) {
      return {
        size: raw.size,
        mode: raw.mode,
        mtimeMs: raw.mtimeMs,
        ctimeMs: raw.ctimeMs,
        birthtimeMs: raw.birthtimeMs,
        mtime: new Date(raw.mtimeMs),
        ctime: new Date(raw.ctimeMs),
        birthtime: new Date(raw.birthtimeMs),
        isFile() { return raw.file; },
        isDirectory() { return raw.directory; },
        isSymbolicLink() { return raw.symlink; },
      };
    },

    makeResponse(raw) {
      const headers = raw.headers || {};
      return {
        status: raw.status,
        statusText: raw.statusText,
        ok: raw.ok,
        url: raw.url,
        redirected: false,
        headers: {
          get(name) {
            const value = headers[String(name).toLowerCase()];
            return value === undefined ? null : value;
          },
          has(name) { return Object.prototype.hasOwnProperty.call(headers, String(name).toLowerCase()); },
          forEach(fn) { for (const key of Object.keys(headers)) fn(headers[key], key, this); },
          entries() { return Object.entries(headers)[Symbol.iterator](); },
          keys() { return Object.keys(headers)[Symbol.iterator](); },
        },
        text() { return Promise.resolve(raw.body); },
        json() { return Promise.resolve().then(() => JSON.parse(raw.body)); },
        arrayBuffer() { return Promise.resolve(raw.bytes.buffer); },
        bytes() { return Promise.resolve(raw.bytes); },
      };
    },
  };

  Object.defineProperty(globalThis, '__tensoragentShim', { value: shim, enumerable: false, configurable: false, writable: false });
})();
""";
}
