/**
 * Cove Frontend Extension Bundle Entry Point
 * Exports components under default.components and actionHandlers under default.actionHandlers
 */

import { BatchScraperModal } from "./BatchScraperModal";

export default {
  components: {
    BatchScraperModal,
  },
  actionHandlers: {
    openBatchScraper: (_action: unknown, payload: Record<string, unknown>) => {
      const rawIds = payload?.selectedIds ?? payload?.entityIds ?? [];
      const videoIds = Array.isArray(rawIds) ? (rawIds as number[]) : [];

      if (videoIds.length > 0) {
        window.dispatchEvent(
          new CustomEvent("cove:open-batch-scraper", {
            detail: { videoIds },
          })
        );
      }

      return { suppressToast: true };
    },
  },
};
