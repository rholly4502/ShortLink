import { Navigate, Route, Routes } from 'react-router-dom'

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

function Home() {
  return (
    <main>
      <h1>ShortLink</h1>
      <p>Kelola tautan pendek dan analytics.</p>
    </main>
  )
}
