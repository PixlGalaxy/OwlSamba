import type { Config } from 'tailwindcss'

export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        surface: '#0f172a',
        card: '#111827',
        accent: '#22d3ee',
      },
    },
  },
  plugins: [],
} satisfies Config
