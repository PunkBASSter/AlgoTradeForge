import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";
import { Providers } from "./providers";
import { NavBar } from "@/components/layout/nav-bar";
import { RunNewProvider } from "@/contexts/run-new-context";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "AlgoTradeForge",
  description: "Algorithmic trading platform",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className="dark">
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased min-h-screen flex flex-col`}
      >
        <Providers>
          <RunNewProvider>
            <NavBar />
            <main className="flex-1">{children}</main>
          </RunNewProvider>

          <footer className="px-6 py-2 border-t border-border-subtle text-text-muted text-xs">
            AlgoTradeForge v0.1.0
          </footer>
        </Providers>
      </body>
    </html>
  );
}
