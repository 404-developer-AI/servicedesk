import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "@tanstack/react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import "./index.css";
import { router } from "@/app/router";
import { bootstrapAuth } from "@/auth/bootstrap";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});

// Fetch /api/auth/me + /api/auth/setup/status before the router mounts so the
// first beforeLoad gates see the real auth state and never flash the wrong
// page. If the network call fails, bootstrapAuth falls through to a safe
// "unauthenticated, no setup" state and the login page surfaces errors itself.
await bootstrapAuth();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
