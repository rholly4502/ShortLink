import { Link, Navigate, Route, Routes } from 'react-router-dom'
import LoginPage from './pages/LoginPage'
import RegisterPage from './pages/RegisterPage'
import LinksPage from './pages/LinksPage'

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/links" element={<LinksPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

function Home() {
  return (
    <main>
      <h1>ShortLink</h1>
      <p>Kelola tautan pendek dan analytics.</p>
      <nav className="home-nav">
        <Link to="/links">Kelola Tautan</Link>
        <Link to="/login">Masuk</Link>
        <Link to="/register">Daftar</Link>
      </nav>
    </main>
  )
}
