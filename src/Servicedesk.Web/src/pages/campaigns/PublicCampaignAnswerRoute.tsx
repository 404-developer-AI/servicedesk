import { useParams } from "@tanstack/react-router";
import { PublicCampaignAnswerPage } from "./PublicCampaignAnswerPage";

/// Lazy route component for `/campaign-answer/$token` (bare shell, no session).
export function PublicCampaignAnswerRoute() {
  const { token } = useParams({ from: "/campaign-answer/$token" });
  return <PublicCampaignAnswerPage token={token} />;
}
