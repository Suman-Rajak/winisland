// Builds /ads.txt from the publisher ID in src/data/ads.js. AdSense needs this file.
import { ADSENSE_CLIENT } from '../data/ads.js';

export function GET() {
  const publisher = ADSENSE_CLIENT.replace(/^ca-/, '');
  const body = publisher
    ? `google.com, ${publisher}, DIRECT, f08c47fec0942fa0\n`
    : '# Add your AdSense publisher ID in src/data/ads.js to fill this file.\n';
  return new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
}
