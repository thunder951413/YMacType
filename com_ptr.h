#pragma once

#include <unknwn.h>
#include <memory>
#include <utility>

// Small ATL-independent COM pointer used by the rendering hooks.  The public
// `p` member intentionally preserves the old CComPtr call sites that inspect a
// COM object's vtable when installing a demand hook.
template <class T>
class CComPtr
{
public:
	T* p;

	CComPtr() noexcept : p(NULL) {}

	CComPtr(T* value) noexcept : p(value)
	{
		InternalAddRef();
	}

	CComPtr(const CComPtr& other) noexcept : p(other.p)
	{
		InternalAddRef();
	}

	CComPtr(CComPtr&& other) noexcept : p(other.p)
	{
		other.p = NULL;
	}

	~CComPtr()
	{
		InternalRelease();
	}

	CComPtr& operator=(T* value) noexcept
	{
		if (p != value) {
			if (value)
				value->AddRef();
			InternalRelease();
			p = value;
		}
		return *this;
	}

	CComPtr& operator=(const CComPtr& other) noexcept
	{
		return operator=(other.p);
	}

	CComPtr& operator=(CComPtr&& other) noexcept
	{
		if (this != std::addressof(other)) {
			InternalRelease();
			p = other.p;
			other.p = NULL;
		}
		return *this;
	}

	T* operator->() const noexcept { return p; }
	operator T*() const noexcept { return p; }
	explicit operator bool() const noexcept { return p != NULL; }

	// Output parameters always take ownership of the returned COM reference.
	T** operator&() noexcept
	{
		InternalRelease();
		return &p;
	}

	void Release() noexcept
	{
		InternalRelease();
	}

private:
	void InternalAddRef() noexcept
	{
		if (p)
			p->AddRef();
	}

	void InternalRelease() noexcept
	{
		T* value = p;
		if (value) {
			p = NULL;
			value->Release();
		}
	}
};
