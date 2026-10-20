// Signs SHA256SUMS.txt with the release key (ECDSA P-256, SHA-256, DER signature) and writes SHA256SUMS.txt.sig (base64).
// usage: node tools/sign-release.cjs [sums-file] [private-key.pem]   (defaults: artifacts/SHA256SUMS.txt, keys/neodm-update-signing.pem)
// The private key is never printed. The public key is embedded in src/NDM.App/UpdateTrust.cs.
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const root = path.join(__dirname, '..');
const sums = process.argv[2] || path.join(root, 'artifacts', 'SHA256SUMS.txt');
const keyFile = process.argv[3] || path.join(root, 'keys', 'neodm-update-signing.pem');
if (!fs.existsSync(keyFile)) { console.error('Missing signing key: ' + keyFile); process.exit(1); }
const key = crypto.createPrivateKey(fs.readFileSync(keyFile));
const data = fs.readFileSync(sums);
const sig = crypto.sign('sha256', data, { key, dsaEncoding: 'der' });
const pub = crypto.createPublicKey(key).export({ type: 'spki', format: 'der' }).toString('base64');
const embedded = (/PublicKeySpkiBase64\s*=\s*"([^"]+)"/.exec(fs.readFileSync(path.join(root, 'src', 'NDM.App', 'UpdateTrust.cs'), 'utf8')) || [])[1];
if (!process.argv[3] && embedded && embedded !== pub) { console.error('The signing key does not match the public key embedded in the app. Refusing to sign.'); process.exit(1); }
fs.writeFileSync(sums + '.sig', sig.toString('base64') + '\n');
const ok = crypto.verify('sha256', data, { key: crypto.createPublicKey(key), dsaEncoding: 'der' }, sig);
console.log('Signed ' + path.basename(sums) + ' -> ' + path.basename(sums) + '.sig (verify: ' + (ok ? 'OK' : 'FAILED') + ', public key SPKI ' + pub.slice(0, 16) + '...)');
process.exit(ok ? 0 : 1);
