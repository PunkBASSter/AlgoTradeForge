import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import Link from "next/link";
import "./globals.css";
import { Providers } from "./providers";

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

const navLinks = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/debug", label: "Debug" },
];

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
          <header className="flex items-center justify-between px-6 py-3 border-b border-border-default bg-bg-surface">
            <Link
              href="/dashboard"
              className="text-lg font-bold text-text-primary tracking-tight"
            >
              AlgoTradeForge
            </Link>
            <nav className="flex items-center gap-6">
              {navLinks.map((link) => (
                <Link
                  key={link.href}
                  href={link.href}
                  className="text-sm text-text-secondary hover:text-text-primary transition-colors"
                >
                  {link.label}
                </Link>
              ))}
            </nav>
          </header>

          <main className="flex-1">{children}</main>

          <footer className="px-6 py-2 border-t border-border-subtle text-text-muted text-xs">
            AlgoTradeForge v0.1.0
          </footer>
        </Providers>
      </body>
    </html>
  );
}
