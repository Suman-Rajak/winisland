// Google AdSense settings. Leave ADSENSE_CLIENT empty until your site is approved: no ad code
// is loaded and the ad slots take no space. In `npm run dev` the slots show as dashed boxes.
//
// 1. Paste your publisher ID, e.g. 'ca-pub-1234567890123456'. This also creates /ads.txt
//    and loads Auto ads if you turn them on in AdSense.
// 2. Optional: create display ad units in AdSense and paste their slot IDs below.
export const ADSENSE_CLIENT = 'ca-pub-8042927022471258';

export const AD_SLOTS = {
  home: '', // home page, below the islands
  belowTool: '', // every tool page, under the tool
  inGuide: '', // inside the guide text
  sidebar: '', // sticky tall ad beside the guide (desktop only)
};
